// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.CommandLine;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools.Test;
using Microsoft.TemplateEngine.Cli.Commands;
using Microsoft.Testing.Platform.Helpers;
using Microsoft.Testing.Platform.OutputDevice;
using Microsoft.Testing.Platform.OutputDevice.Terminal;
using Microsoft.Testing.TestInfrastructure;

namespace Microsoft.DotNet.Cli
{
    internal partial class TestingPlatformCommand : CliCommand, ICustomHelp
    {
        private readonly ConcurrentBag<TestApplication> _testApplications = [];
        private readonly CancellationTokenSource _cancellationToken = new();

        private MSBuildConnectionHandler _msBuildConnectionHandler;
        private TestModulesFilterHandler _testModulesFilterHandler;
        private TerminalTestReporter _output;
        private TestApplicationActionQueue _actionQueue;
        private Task _namedPipeConnectionLoop;
        private List<string> _args;
        private Dictionary<TestApplication, (string ModulePath, string TargetFramework, string Architecture, string ExecutionId)> _executions = new();

        public TestingPlatformCommand(string name, string description = null) : base(name, description)
        {
            TreatUnmatchedTokensAsErrors = false;
        }

        public int Run(ParseResult parseResult)
        {
            if (Environment.GetEnvironmentVariable("Debug") == "1")
            {
                DebuggerUtility.AttachCurrentProcessToParentVSProcess();
            }

            if (parseResult.HasOption(TestingPlatformOptions.ArchitectureOption))
            {
                VSTestTrace.SafeWriteTrace(() => $"The --arch option is not yet supported.");
                return ExitCodes.GenericFailure;
            }

            // User can decide what the degree of parallelism should be
            // If not specified, we will default to the number of processors
            if (!int.TryParse(parseResult.GetValue(TestingPlatformOptions.MaxParallelTestModulesOption), out int degreeOfParallelism))
                degreeOfParallelism = Environment.ProcessorCount;

            bool filterModeEnabled = parseResult.HasOption(TestingPlatformOptions.TestModulesFilterOption);

            if (filterModeEnabled && parseResult.HasOption(TestingPlatformOptions.ArchitectureOption))
            {
                VSTestTrace.SafeWriteTrace(() => $"The --arch option is not supported yet.");
            }

            BuiltInOptions builtInOptions = new(
                parseResult.HasOption(TestingPlatformOptions.NoRestoreOption),
                parseResult.HasOption(TestingPlatformOptions.NoBuildOption),
                parseResult.GetValue(TestingPlatformOptions.ConfigurationOption),
                parseResult.GetValue(TestingPlatformOptions.ArchitectureOption));

            var console = new SystemConsole();
            var output = new TerminalTestReporter(console, new TerminalTestReporterOptions()
            {
                ShowPassedTests = Environment.GetEnvironmentVariable("SHOW_PASSED") == "1",
                ShowProgress = () => Environment.GetEnvironmentVariable("NO_PROGRESS") != "1",
                UseAnsi = Environment.GetEnvironmentVariable("NO_ANSI") != "1",
                ShowAssembly = true,
                ShowAssemblyStartAndComplete = true,
            });
            _output = output;
            _output.TestExecutionStarted(DateTimeOffset.Now, degreeOfParallelism);

            if (ContainsHelpOption(parseResult.GetArguments()))
            {
                _actionQueue = new(degreeOfParallelism, async (TestApplication testApp) =>
                {
                    testApp.HelpRequested += OnHelpRequested;
                    testApp.ErrorReceived += OnErrorReceived;
                    testApp.TestProcessExited += OnTestProcessExited;
                    testApp.Run += OnTestApplicationRun;
                    testApp.ExecutionIdReceived += OnExecutionIdReceived;

                    var result = await testApp.RunAsync(filterModeEnabled, enableHelp: true, builtInOptions);
                    _output.TestExecutionCompleted(DateTimeOffset.Now);
                    return result;
                });
            }
            else
            {
                _actionQueue = new(degreeOfParallelism, async (TestApplication testApp) =>
                {
                    testApp.HandshakeReceived += OnHandshakeReceived;
                    testApp.DiscoveredTestsReceived += OnDiscoveredTestsReceived;
                    testApp.TestResultsReceived += OnTestResultsReceived;
                    testApp.FileArtifactsReceived += OnFileArtifactsReceived;
                    testApp.SessionEventReceived += OnSessionEventReceived;
                    testApp.ErrorReceived += OnErrorReceived;
                    testApp.TestProcessExited += OnTestProcessExited;
                    testApp.Run += OnTestApplicationRun;
                    testApp.ExecutionIdReceived += OnExecutionIdReceived;

                    return await testApp.RunAsync(filterModeEnabled, enableHelp: false, builtInOptions);
                });
            }

            _args = new List<string>(parseResult.UnmatchedTokens);
            _msBuildConnectionHandler = new(_args, _actionQueue);
            _testModulesFilterHandler = new(_args, _actionQueue);
            _namedPipeConnectionLoop = Task.Run(async () => await _msBuildConnectionHandler.WaitConnectionAsync(_cancellationToken.Token));

            if (parseResult.HasOption(TestingPlatformOptions.TestModulesFilterOption))
            {
                if (!_testModulesFilterHandler.RunWithTestModulesFilter(parseResult))
                {
                    _output.TestExecutionCompleted(DateTimeOffset.Now);
                    return ExitCodes.GenericFailure;
                }
            }
            else
            {
                // If no filter was provided, MSBuild will get the test project paths
                var msbuildResult = _msBuildConnectionHandler.RunWithMSBuild(parseResult);
                if (msbuildResult != 0)
                {
                    VSTestTrace.SafeWriteTrace(() => $"MSBuild task _GetTestsProject didn't execute properly with exit code: {msbuildResult}.");
                    _output.TestExecutionCompleted(DateTimeOffset.Now);
                    return ExitCodes.GenericFailure;
                }
            }

            _actionQueue.EnqueueCompleted();
            var hasFailed = _actionQueue.WaitAllActions();

            // Above line will block till we have all connections and all GetTestsProject msbuild task complete.
            _cancellationToken.Cancel();
            _namedPipeConnectionLoop.Wait();

            // Clean up everything
            CleanUp();

            _output.TestExecutionCompleted(DateTimeOffset.Now);
            return hasFailed ? ExitCodes.GenericFailure : ExitCodes.Success;
        }

        private void CleanUp()
        {
            _msBuildConnectionHandler.Dispose();
            foreach (var testApplication in _testApplications)
            {
                testApplication.Dispose();
            }
        }

        private void OnHandshakeReceived(object sender, HandshakeArgs args)
        {
            var testApplication = (TestApplication)sender;
            var executionId = args.Handshake.Properties[HandshakeMessagePropertyNames.ExecutionId];
            var arch = args.Handshake.Properties[HandshakeMessagePropertyNames.Architecture]?.ToLower();
            var tfm = TargetFrameworkParser.GetShortTargetFramework(args.Handshake.Properties[HandshakeMessagePropertyNames.Framework]);
            (string ModulePath, string TargetFramework, string Architecture, string ExecutionId) appInfo = new(testApplication.Module.DLLPath, tfm, arch, executionId);
            _executions[testApplication] = appInfo;
            _output.AssemblyRunStarted(appInfo.ModulePath, appInfo.TargetFramework, appInfo.Architecture, appInfo.ExecutionId);

            if (!VSTestTrace.TraceEnabled)
            {
                return;
            }

            var handshake = args.Handshake;

            foreach (var property in handshake.Properties)
            {
                VSTestTrace.SafeWriteTrace(() => $"{property.Key}: {property.Value}");
            }
        }

        private void OnDiscoveredTestsReceived(object sender, DiscoveredTestEventArgs args)
        {
            if (!VSTestTrace.TraceEnabled)
            {
                return;
            }

            var discoveredTestMessages = args.DiscoveredTests;

            VSTestTrace.SafeWriteTrace(() => $"DiscoveredTests Execution Id: {args.ExecutionId}");
            foreach (DiscoveredTest discoveredTestMessage in discoveredTestMessages)
            {
                VSTestTrace.SafeWriteTrace(() => $"DiscoveredTest: {discoveredTestMessage.Uid}, {discoveredTestMessage.DisplayName}");
            }
        }

        private void OnTestResultsReceived(object sender, TestResultEventArgs args)
        {
            foreach (var testResult in args.SuccessfulTestResults)
            {

                var testApp = (TestApplication)sender;
                var appInfo = _executions[testApp];
                // TODO: timespan for duration
                _output.TestCompleted(appInfo.ModulePath, appInfo.TargetFramework, appInfo.Architecture, appInfo.ExecutionId,
                    testResult.DisplayName,
                    TestOutcome.Passed,
                    TimeSpan.FromSeconds(1),
                    errorMessage: null,
                    errorStackTrace: null,
                    expected: null,
                    actual: null);
            }

            foreach (var testResult in args.FailedTestResults)
            {

                var testApp = (TestApplication)sender;
                // TODO: timespan for duration
                // TODO: expected
                // TODO: actual
                var appInfo = _executions[testApp];
                _output.TestCompleted(appInfo.ModulePath, appInfo.TargetFramework, appInfo.Architecture, appInfo.ExecutionId,
                    testResult.DisplayName,
                    TestOutcome.Fail,
                    TimeSpan.FromSeconds(1),
                    errorMessage: testResult.ErrorMessage,
                    errorStackTrace: testResult.ErrorStackTrace,
                    expected: null, actual: null);
            }


            if (!VSTestTrace.TraceEnabled)
            {
                return;
            }

            VSTestTrace.SafeWriteTrace(() => $"TestResults Execution Id: {args.ExecutionId}");

            foreach (SuccessfulTestResult successfulTestResult in args.SuccessfulTestResults)
            {
                VSTestTrace.SafeWriteTrace(() => $"SuccessfulTestResult: {successfulTestResult.Uid}, {successfulTestResult.DisplayName}, " +
                $"{successfulTestResult.State}, {successfulTestResult.Reason}, {successfulTestResult.SessionUid}");
            }

            foreach (FailedTestResult failedTestResult in args.FailedTestResults)
            {
                VSTestTrace.SafeWriteTrace(() => $"FailedTestResult: {failedTestResult.Uid}, {failedTestResult.DisplayName}, " +
                $"{failedTestResult.State}, {failedTestResult.Reason}, {failedTestResult.ErrorMessage}," +
                $" {failedTestResult.ErrorStackTrace}, {failedTestResult.SessionUid}");
            }
        }

        private void OnFileArtifactsReceived(object sender, FileArtifactEventArgs args)
        {
            if (!VSTestTrace.TraceEnabled)
            {
                return;
            }

            VSTestTrace.SafeWriteTrace(() => $"FileArtifactMessages Execution Id: {args.ExecutionId}");

            foreach (FileArtifact fileArtifactMessage in args.FileArtifacts)
            {
                VSTestTrace.SafeWriteTrace(() => $"FileArtifacr: {fileArtifactMessage.FullPath}, {fileArtifactMessage.DisplayName}, " +
                $"{fileArtifactMessage.Description}, {fileArtifactMessage.TestUid}, {fileArtifactMessage.TestDisplayName}, " +
                $"{fileArtifactMessage.SessionUid}");
            }
        }

        private void OnSessionEventReceived(object sender, SessionEventArgs args)
        {
            if (!VSTestTrace.TraceEnabled)
            {
                return;
            }

            var sessionEvent = args.SessionEvent;
            VSTestTrace.SafeWriteTrace(() => $"TestSessionEvent: {sessionEvent.SessionType}, {sessionEvent.SessionUid}, {sessionEvent.ExecutionId}");
        }

        private void OnErrorReceived(object sender, ErrorEventArgs args)
        {
            if (!VSTestTrace.TraceEnabled)
            {
                return;
            }

            VSTestTrace.SafeWriteTrace(() => args.ErrorMessage);
        }

        private void OnTestProcessExited(object sender, TestProcessExitEventArgs args)
        {
            var testApplication = (TestApplication)sender;

            var appInfo = _executions[testApplication];
            _output.AssemblyRunCompleted(appInfo.ModulePath, appInfo.TargetFramework, appInfo.Architecture, appInfo.ExecutionId);

            if (!VSTestTrace.TraceEnabled)
            {
                return;
            }

            if (args.ExitCode != 0)
            {
                VSTestTrace.SafeWriteTrace(() => $"Test Process exited with non-zero exit code: {args.ExitCode}");
            }

            if (args.OutputData.Count > 0)
            {
                VSTestTrace.SafeWriteTrace(() => $"Output Data: {string.Join("\n", args.OutputData)}");
            }

            if (args.ErrorData.Count > 0)
            {
                VSTestTrace.SafeWriteTrace(() => $"Error Data: {string.Join("\n", args.ErrorData)}");
            }
        }

        private void OnTestApplicationRun(object sender, EventArgs args)
        {
            TestApplication testApp = sender as TestApplication;
            _testApplications.Add(testApp);
        }

        private void OnExecutionIdReceived(object sender, ExecutionEventArgs args)
        {
        }

        private static bool ContainsHelpOption(IEnumerable<string> args) => args.Contains(CliConstants.HelpOptionKey) || args.Contains(CliConstants.HelpOptionKey.Substring(0, 2));
    }
}
