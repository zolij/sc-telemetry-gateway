# Third-party components

The Windows distributions include these runtime components. Dependency
versions and hashes are recorded in the projects' NuGet lock files.

| Component | Version | License / notices |
| --- | --- | --- |
| Google.Protobuf | 3.36.2 | [BSD-3-Clause](licenses/Google.Protobuf.txt) |
| Grpc.Net.Client, Grpc.Net.Common, Grpc.Core.Api | 2.85.0 | [Apache-2.0](../LICENSE), Copyright 2019 The gRPC Authors |
| Microsoft.Extensions.Logging.Abstractions | 8.0.1 | [MIT](licenses/Microsoft.Extensions.txt), [notices](licenses/Microsoft.Extensions.NOTICES.txt) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | [MIT](licenses/Microsoft.Extensions.txt), [notices](licenses/Microsoft.Extensions.NOTICES.txt) |
| QRCoder | 1.6.0 | [MIT](licenses/QRCoder.txt) |
| System.Security.Cryptography.ProtectedData | 11.0.0 | [MIT](licenses/ProtectedData.txt), [notices](licenses/ProtectedData.NOTICES.txt) |
| .NET runtime | 10.0.15 | [MIT](licenses/DotnetRuntime.txt), [notices](licenses/DotnetRuntime.NOTICES.txt) |
| Windows Desktop runtime | 10.0.15 | [MIT](licenses/WindowsDesktop.txt), applicable .NET component notices above |
| Velopack library, installer and updater | 1.2.161 | [MIT](licenses/Velopack.txt) |

Build/test tools (including Grpc.Tools, xUnit, ASP.NET Core test hosting and
JsonSchema.Net) are not shipped as application dependencies. Their NuGet
metadata contains their respective licenses. Protocol interoperability
provenance and the project's independence are documented in [NOTICE](../NOTICE).

When updating dependencies or the self-contained runtime, review the resolved
versions and refresh their license/notice texts before distributing a new build.
