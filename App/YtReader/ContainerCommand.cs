﻿using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using Mutuo.Etl.Pipe;
using Serilog;
using SysExtensions;
using SysExtensions.Collections;
using SysExtensions.Text;
using static YtReader.ContainerCommand.Options;

namespace YtReader {
  public record YtContainerRunner(AzureContainers Az, ContainerCfg ContainerCfg, PipeAppCtx Ctx, CliEntry Cli, ILogger Log) {
    public async Task Run(string groupName, string fullImageName = null, CancellationToken cancel = default, bool returnOnStart = false) =>
      await Az.RunContainer(groupName, fullImageName ?? ContainerCfg.FullContainerImageName(await Az.FindImageTag(ContainerCfg.ImageName)),
        Ctx.EnvironmentVariables, LocalArgs(), returnOnStart, "./recfluence", log: Log, cancel: cancel);

    string[] LocalArgs() {
      bool ShouldStrip(string arg, string prev) => AllArgs.Contains(arg) || AllFlags.Contains(arg) || 
                                                   (!arg.StartsWith("-") &&  AllArgs.Contains(prev)); // remove values subsequent to the args
      
      return Cli.Args?.Select((a, i) => (a, prev: i > 0 ? Cli.Args[i - 1] : null))
        .Where(a => !ShouldStrip(a.a, a.prev)) 
        .Select(a => a.a).ToArray();
    }
  }

  /// <summary>A command that has the option to run on ACS instead. Nice to get help/configuration context on the command
  ///   line than using AzCli</summary>
  public abstract record ContainerCommand(ContainerCfg ContainerCfg, YtContainerRunner Runner, ILogger Log) : ICommand {
    public static class Options {
      public const char   RunInContainer = 'z';
      public const string Tag            = "container-tag";
      public const string GroupName      = "container-name";
      
      public static readonly HashSet<string> AllFlags = new[] {RunInContainer}.Select(p => $"-{p}").ToHashSet();
      public static readonly HashSet<string> AllArgs = new[] {Tag, GroupName}.Select(p => $"--{p}").ToHashSet();
    }

    [CommandOption(RunInContainer, IsRequired = false)]
    public bool RunOnContainer { get;                                     set; }
    [CommandOption(Tag)]               public string ContainerTag  { get; set; }
    [CommandOption(Options.GroupName)] public string ContainerName { get; set; }

    public async ValueTask ExecuteAsync(IConsole console) {
      if (RunOnContainer) {
        var image = ContainerTag.HasValue() ? ContainerCfg.FullContainerImageName(ContainerTag) : null;
        await Runner.Run(ContainerName ?? GroupName, image, console.GetCancellationToken(), returnOnStart: true);
        return;
      }

      await ExecuteLocal(console)
        .WithOnError(ex => Log.Error(ex, "Unhandled error running command {Command}: {Message}", GetType().Name, ex.Message));
    }

    protected abstract string GroupName { get; }
    protected abstract ValueTask ExecuteLocal(IConsole console);
  }
}