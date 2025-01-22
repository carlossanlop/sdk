// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.DotNet.ApiSymbolExtensions;
using Microsoft.DotNet.ApiSymbolExtensions.Filtering;
using Microsoft.DotNet.ApiSymbolExtensions.Logging;
using Microsoft.DotNet.GenAPI;

namespace Microsoft.DotNet.ApiDiff.Tool;

/// <summary>
/// Entrypoint for the genapidiff tool, which generates a markdown diff of two
/// different versions of the same assembly, using the specified command line options.
/// </summary>
public static class Program
{
    private static readonly string[] defaultAttributesToExclude = new[]
    {
        "T:System.AttributeUsageAttribute",
        "T:System.ComponentModel.EditorBrowsableAttribute",
        "T:System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute",
        "T:System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"
    };

    public static async Task Main(string[] args)
    {
        RootCommand rootCommand = new("genapidiff");

        Option<bool> optionAddPartialModifier = new(["--addPartialModifier", "-pm"], () => false)
        {
            Description = "Add the 'partial' modifier for types.",
            IsRequired = false
        };

        Option<string> optionAfterAssembliesFolderPath = new(["--after", "-a"])
        {
            Description = "The path to the folder containing the new (after) assemblies.",
            Arity = ArgumentArity.ExactlyOne,
            IsRequired = false
        };

        Option<string> optionAfterRefAssembliesFolderPath = new(["--refafter", "-ra"])
        {
            Description = "The path to the folder containing the new (after) reference assemblies.",
            Arity = ArgumentArity.ExactlyOne,
            IsRequired = false
        };

        Option<string[]> optionAttributesToExclude = new(["--attributesToExclude", "-ate"], () => defaultAttributesToExclude)
        {
            Description = "Attributes to exclude from the diff.",
            Arity = ArgumentArity.ZeroOrMore,
            IsRequired = false
        };

        Option<string> optionBeforeAssembliesFolderPath = new(["--before", "-b"])
        {
            Description = "The path to the folder containing the old (before) assemblies.",
            Arity = ArgumentArity.ExactlyOne,
            IsRequired = true
        };

        Option<string> optionBeforeRefAssembliesFolderPath = new(["--refbefore", "-rb"])
        {
            Description = "The path to the folder containing the old (before) reference assemblies.",
            Arity = ArgumentArity.ExactlyOne,
            IsRequired = false
        };

        Option<bool> optionCreateOneFilePerNamespace = new(["--createOneFilePerNamespace", "-fn"], () => true)
        {
            Description = "Create one file for each namespace."
        };

        Option<bool> optionDebug = new(["--debug"], () => false)
        {
            Description = "Stops the tool at startup, prints the process ID and waits for a debugger to attach."
        };

        Option<bool> optionHideImplicitDefaultConstructors = new(["--hideImplicitDefaultConstructors", "-hidc"], () => false)
        {
            Description = "Hide implicit default constructors from types."
        };

        Option<bool> optionHighlightOverridesAndEIIs = new(["--highlightOverridesAndEIIs", "-oii"], () => true)
        {
            Description = "Highlight members that are overrides or explicit interface implementations of a base member."
        };

        Option<bool> optionIncludeAddedAPIs = new(["--includeAdded", "-ia"], () => true)
        {
            Description = "Include members, types and namespaces that were added."
        };

        Option<bool> optionIncludeChangedAPIs = new(["--includeChanged", "-ic"], () => true)
        {
            Description = "Include members, types and namespaces that were changed."
        };

        Option<bool> optionIncludeRemovedAPIs = new(["--includeRemoved", "-ir"], () => true)
        {
            Description = "Include members, types and namespaces that were removed."
        };

        Option<bool> optionIncludeTableOfContents = new(["--includeTableOfContents", "-toc"], () => true)
        {
            Description = "Include a markdown file at the root output folder with a table of contents."
        };

        Option<string> optionOutputFolderPath = new(["--output", "-o"])
        {
            Description = "The path to the output folder.",
            Arity = ArgumentArity.ExactlyOne,
            IsRequired = true
        };

        Option<bool> optionShowChangedAttributes = new(["--showChangedAttributes", "-att"], () => true)
        {
            Description = "Show added, changed or removed attributes."
        };

        Option<bool> optionShowMembersOfChangedTypes = new(["--showAllMembersOfChangedTypes", "-sam"], () => true)
        {
            Description = "Show all members of a type that was added or removed."
        };

        // Custom ordering for the help menu.
        rootCommand.Add(optionBeforeAssembliesFolderPath);
        rootCommand.Add(optionBeforeRefAssembliesFolderPath);
        rootCommand.Add(optionAfterAssembliesFolderPath);
        rootCommand.Add(optionAfterRefAssembliesFolderPath);
        rootCommand.Add(optionOutputFolderPath);
        rootCommand.Add(optionAttributesToExclude);
        rootCommand.Add(optionCreateOneFilePerNamespace);
        rootCommand.Add(optionHighlightOverridesAndEIIs);
        rootCommand.Add(optionIncludeAddedAPIs);
        rootCommand.Add(optionIncludeChangedAPIs);
        rootCommand.Add(optionIncludeRemovedAPIs);
        rootCommand.Add(optionIncludeTableOfContents);
        rootCommand.Add(optionShowChangedAttributes);
        rootCommand.Add(optionShowMembersOfChangedTypes);
        rootCommand.Add(optionAddPartialModifier);
        rootCommand.Add(optionHideImplicitDefaultConstructors);
        rootCommand.Add(optionDebug);

        GenAPIDiffConfigurationBinder c = new(optionAddPartialModifier,
                                              optionAfterAssembliesFolderPath,
                                              optionAfterRefAssembliesFolderPath,
                                              optionAttributesToExclude,
                                              optionBeforeAssembliesFolderPath,
                                              optionBeforeRefAssembliesFolderPath,
                                              optionCreateOneFilePerNamespace,
                                              optionDebug,
                                              optionHideImplicitDefaultConstructors,
                                              optionHighlightOverridesAndEIIs,
                                              optionIncludeAddedAPIs,
                                              optionIncludeChangedAPIs,
                                              optionIncludeRemovedAPIs,
                                              optionIncludeTableOfContents,
                                              optionOutputFolderPath,
                                              optionShowChangedAttributes,
                                              optionShowMembersOfChangedTypes);

        rootCommand.SetHandler(HandleCommand, c);
        await rootCommand.InvokeAsync(args);
    }

    private static void HandleCommand(DiffConfiguration diffConfig)
    {
        var logger = new ConsoleLog(MessageImportance.Normal);

        // Custom ordering to match help menu.
        logger.LogMessage("Selected options:");
        logger.LogMessage($" - 'Before' assemblies:                {diffConfig.BeforeAssembliesFolderPath}");
        logger.LogMessage($" - 'Before' reference assemblies:      {diffConfig.BeforeAssemblyReferencesFolderPath}");
        logger.LogMessage($" - 'After' assemblies:                 {diffConfig.AfterAssembliesFolderPath}");
        logger.LogMessage($" - 'After' ref assemblies:             {diffConfig.AfterAssemblyReferencesFolderPath}");
        logger.LogMessage($" - Output:                             {diffConfig.OutputFolderPath}");
        logger.LogMessage($" - Attributes to exclude:              {string.Join(", ", diffConfig.AttributesToExclude)}");
        logger.LogMessage($" - Include added APIs:                 {diffConfig.IncludeAddedAPIs}");
        logger.LogMessage($" - Include changed APIs:               {diffConfig.IncludeChangedAPIs}");
        logger.LogMessage($" - Include removed APIs:               {diffConfig.IncludeRemovedAPIs}");
        logger.LogMessage($" - Include table of contents:          {diffConfig.IncludeTableOfContents}");
        logger.LogMessage($" - Create one file per namespace:      {diffConfig.CreateOneFilePerNamespace}");
        logger.LogMessage($" - Show members of changed types:      {diffConfig.ShowMembersOfChangedTypes}");
        logger.LogMessage($" - Highlight overrides and EIIs:       {diffConfig.HightlightOverridesAndEIIs}");
        logger.LogMessage($" - Show changed attributes:            {diffConfig.ShowChangedAttributes}");
        logger.LogMessage($" - Add partial modifier to types:      {diffConfig.AddPartialModifier}");
        logger.LogMessage($" - Hide implicit default constructors: {diffConfig.HideImplicitDefaultConstructors}");
        logger.LogMessage($" - Debug:                              {diffConfig.Debug}");
        logger.LogMessage("");

        (IAssemblySymbolLoader beforeLoader, Dictionary<string, IAssemblySymbol> beforeAssemblySymbols) = GetLoaderAndSymbols(
            logger, diffConfig.BeforeAssembliesFolderPath, diffConfig.BeforeAssemblyReferencesFolderPath);

        (IAssemblySymbolLoader afterLoader, Dictionary<string, IAssemblySymbol> afterAssemblySymbols) = GetLoaderAndSymbols(
            logger, diffConfig.AfterAssembliesFolderPath, diffConfig.AfterAssemblyReferencesFolderPath);

        DiffGenerator generator = new(logger,
                                      beforeLoader,
                                      afterLoader,
                                      CompositeSymbolFilter.GetSymbolFilterFromList([]),
                                      CompositeSymbolFilter.GetAttributeFilterFromList(diffConfig.AttributesToExclude),
                                      header: string.Empty,
                                      exceptionMessage: null,
                                      includeAssemblyAttributes: false,
                                      addPartialModifier: diffConfig.AddPartialModifier,
                                      hideImplicitDefaultConstructors: diffConfig.HideImplicitDefaultConstructors);

        if (diffConfig.Debug)
        {
            WaitForDebugger();
        }

        Dictionary<string, string> results = generator.Run(beforeAssemblySymbols, afterAssemblySymbols);

        Directory.CreateDirectory(diffConfig.OutputFolderPath);
        foreach ((string assemblyName, string text) in results)
        {
            string filePath = Path.Combine(diffConfig.OutputFolderPath, $"{assemblyName}.md");
            File.WriteAllText(filePath, text);
            logger.LogMessage($"Wrote '{filePath}'.");
        }
    }

    private static void WaitForDebugger()
    {
        while (!Debugger.IsAttached)
        {
            Console.WriteLine($"Attach to process {Environment.ProcessId}...");
            Thread.Sleep(1000);
        }
        Console.WriteLine("Debugger attached!");
        Debugger.Break();
    }

    private static (IAssemblySymbolLoader, Dictionary<string, IAssemblySymbol>) GetLoaderAndSymbols(ILog logger, string assembliesFolderPath, string? refAssembliesFolderPath)
    {
        return AssemblySymbolLoader.CreateFromFiles(
                logger,
                assembliesPaths: [assembliesFolderPath],
                assemblyReferencesPaths: refAssembliesFolderPath != null ? [refAssembliesFolderPath] : null);
    }
}
