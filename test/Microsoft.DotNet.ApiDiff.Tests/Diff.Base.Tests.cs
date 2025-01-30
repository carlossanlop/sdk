// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Microsoft.DotNet.ApiSymbolExtensions;
using Microsoft.DotNet.ApiSymbolExtensions.Filtering;
using Microsoft.DotNet.ApiSymbolExtensions.Logging;
using Microsoft.DotNet.GenAPI;
using Microsoft.DotNet.GenAPI.Tests;

namespace Microsoft.DotNet.ApiDiff.Tests;

public abstract class DiffBaseTests
{
    private readonly ConsoleLog _logger = new(MessageImportance.High);
    protected const string AssemblyName = "MyAssembly.dll";
    private static MetadataReference[] MetadataReferences { get; } = [MetadataReference.CreateFromFile(typeof(object).Assembly!.Location!)];

    protected void RunTest(string beforeCode, string afterCode, string expectedCode, bool addPartialModifier = false, bool hideImplicitDefaultConstructors = false) =>
        RunTest(before: [(AssemblyName, beforeCode)], after: [(AssemblyName, afterCode)], expected: new() { { AssemblyName, expectedCode } }, addPartialModifier, hideImplicitDefaultConstructors);

    protected void RunTest((string, string)[] before, (string, string)[] after, Dictionary<string, string> expected, bool addPartialModifier = false, bool hideImplicitDefaultConstructors = false)
    {
        // while (!System.Diagnostics.Debugger.IsAttached)
        // {
        //     System.Console.WriteLine($"Waiting for debugger to attach to {Environment.ProcessId}");
        //     System.Threading.Thread.Sleep(1000);
        // }
        // System.Console.WriteLine("Attached");
        // System.Diagnostics.Debugger.Break();

        string[] typesToExclude = Array.Empty<string>();
        string[] attributesToExclude = Array.Empty<string>();

        // CreateFromTexts will assert on any loader diagnostics via SyntaxFactory.
        (IAssemblySymbolLoader beforeLoader, Dictionary<string, IAssemblySymbol> beforeAssemblySymbols) = TestAssemblyLoaderFactory
            .CreateFromTexts(_logger, assemblyTexts: before, diagnosticOptions: DiffGenerator.BasicDiagnosticOptions);

        (IAssemblySymbolLoader afterLoader, Dictionary<string, IAssemblySymbol> afterAssemblySymbols) = TestAssemblyLoaderFactory
            .CreateFromTexts(_logger, assemblyTexts: after, diagnosticOptions: DiffGenerator.BasicDiagnosticOptions);

        using MemoryStream outputStream = new();
        DiffGenerator generator = new(new ConsoleLog(MessageImportance.Normal),
                                      beforeLoader,
                                      afterLoader,
                                      SymbolFilterFactory.GetFilterFromList(typesToExclude),
                                      SymbolFilterFactory.GetFilterFromList(attributesToExclude),
                                      header: string.Empty,
                                      exceptionMessage: null,
                                      includeAssemblyAttributes: false,
                                      addPartialModifier,
                                      hideImplicitDefaultConstructors,
                                      diagnosticOptions: DiffGenerator.BasicDiagnosticOptions,
                                      MetadataReferences);

        Dictionary<string, string> actualResults = generator.Run(beforeAssemblySymbols, afterAssemblySymbols);

        foreach ((string expectedAssemblyName, string expectedCode) in expected)
        {
            Assert.True(actualResults.TryGetValue(expectedAssemblyName, out string? actualCode), $"Expected assembly entry not found among actual results: {expectedAssemblyName}");
            string fullExpectedCode = GetExpected(expectedCode, expectedAssemblyName);
            Assert.True(fullExpectedCode.Equals(actualCode), $"\nExpected:\n{fullExpectedCode}\nActual:\n{actualCode}");
        }
    }

    private static string GetExpected(string expectedCode, string expectedAssemblyName)
    {
        return $"""
                # {Path.GetFileNameWithoutExtension(expectedAssemblyName)}

                ```diff
                {expectedCode}
                ```

                """;
    }
}
