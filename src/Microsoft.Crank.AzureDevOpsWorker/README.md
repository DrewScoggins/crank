# Azure DevOps worker

`crank-azdo` consumes Crank jobs from an Azure Service Bus queue and reports their
status and output to Azure DevOps.

## External post-processing

The worker can invoke one trusted, separately deployed executable after a
successful Crank attempt. The executable runs before the attempt directory is
deleted and uses that directory as its working directory, so files produced by
Crank, such as `crank-results.json`, are available.

Install the executable on each worker host or in the worker container. Configure
an absolute path when practical:

```text
crank-azdo ... --post-process-executable C:\tools\result-exporter.exe
```

On Linux:

```text
crank-azdo ... --post-process-executable /opt/crank/result-exporter
```

The path can instead be supplied through
`CRANK_AZDO_POST_PROCESS_EXECUTABLE`. The command-line option takes precedence.
Use a dedicated executable rather than a shell or general-purpose runtime,
because payload arguments are passed directly to this trusted executable.

Post-processing has a worker-controlled timeout of 10 minutes by default. Set
`--post-process-timeout <timespan>` or
`CRANK_AZDO_POST_PROCESS_TIMEOUT` to a positive .NET `TimeSpan`, for example
`00:20:00`. The command-line option takes precedence.

### Payload contract

Jobs that need the hook add an optional `postProcess` object:

```json
{
  "name": "crank",
  "args": ["--json", "crank-results.json"],
  "postProcess": {
    "name": "Result export",
    "enabled": true,
    "args": ["upload", "--crank-json", "crank-results.json"]
  }
}
```

`name` is used only as a sanitized display name. `enabled` is optional and
defaults to `true`. When it is `false`, the worker logs that the named hook is
disabled and treats the post-process step as successful without requiring or
starting an executable or evaluating its arguments and cancellation callback.
When enabled, `args` is passed with `ProcessStartInfo.ArgumentList`; it never
selects the executable. Payloads without `postProcess` retain their existing
behavior. A payload that requests enabled post-processing fails explicitly when
no executable is configured.

The worker forwards the executable's standard output and standard error to the
Azure DevOps task log, but does not log its full argument list. The executable
is responsible for avoiding secrets in its own output. A nonzero exit code,
startup failure, timeout, or task cancellation fails the attempt. Ordinary
failures and timeouts use the job's existing retry count; cancellation stops
further attempts. Cleanup still runs after every attempt.
