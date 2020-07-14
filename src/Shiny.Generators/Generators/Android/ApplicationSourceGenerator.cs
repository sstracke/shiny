﻿using System;
using System.Linq;

using Microsoft.CodeAnalysis;

using Uno.RoslynHelpers;
using Uno.SourceGeneration;


namespace Shiny.Generators.Generators.Android
{
    public static class ApplicationSourceGenerator
    {
        public static void Execute(SourceGeneratorContext context)
        {
            var log = context.GetLogger();

            // if application exists, error or override? - could also search for attribute?
            var appClass = context.Compilation.GetTypeByMetadataName("Android.App.Application");
            if (appClass == null)
                return;

            // TODO: log error if application already exists?
            var startupClasses = context
                .GetAllImplementationsOfType<IShinyStartup>()
                .ToList();

            INamedTypeSymbol? startupClass = null;
            switch (startupClasses.Count)
            {
                case 0:
                    log.Warn("No Shiny Startup implementation found");
                    return;
                
                case 1:
                    startupClass = startupClasses.First();
                    break;

                default:
                    log.Error(startupClasses.Count + " Shiny Startup implementations found");
                    return;
            }

            var builder = new IndentedStringBuilder();
            builder.AppendNamespaces("Android.App", "Android.Runtime");

            using (builder.BlockInvariant($"namespace {context.Compilation.GlobalNamespace.Name}"))
            {
                builder.AppendLineInvariant("[ApplicationAttribute]");
                using (builder.BlockInvariant("public partial class AppShinyApplication : Application"))
                {
                    builder.AppendLineInvariant("public AppShinyApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) {}");
                    using (builder.BlockInvariant("public override void OnCreate()"))
                    {
                        builder.AppendLineInvariant($"this.InitShiny({startupClass.ToDisplayString()});");

                        if (context.Compilation.GetTypeByMetadataName("Xamarin.Essentials.Platform") != null)
                            builder.AppendLine("Xamarin.Essentials.Platform.Init(this);");

                        builder.AppendLineInvariant("base.OnCreate();");
                    }

                    using (builder.BlockInvariant("public override void OnTrimMemory([GeneratedEnum] TrimMemory level)"))
                    {
                        builder.AppendLineInvariant("AndroidShinyHost.OnBackground(level);");
                        builder.AppendLineInvariant("base.OnTrimMemory(level);");
                    }
                }
            }
            context.AddCompilationUnit("MainApplication", builder.ToString());
        }
    }
}