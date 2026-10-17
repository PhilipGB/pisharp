# PiSharp native extension fixture

This independent authoring project references only `PiSharp.Extensions.Abstractions` plus one local private dependency project. Normal `dotnet pack` produces the compiled extension DLL, package-root `pisharp-extension.json`, the extension `.deps.json`, and the private dependency DLL.

Pack the v1 fixture:

```sh
dotnet pack tests/fixtures/NativeExtensionFixture/NativeExtensionFixture.csproj \
  --configuration Release --output artifacts/native-extension/v1 \
  -p:NativeFixtureRevision=v1 -p:NativeExtensionVersion=1.0.0 \
  -p:NativeFixtureDependencyVersion=1.0.0
```

Pack an updated v2 fixture with the same package ID:

```sh
dotnet pack tests/fixtures/NativeExtensionFixture/NativeExtensionFixture.csproj \
  --configuration Release --output artifacts/native-extension/v2 \
  -p:NativeFixtureRevision=v2 -p:NativeExtensionVersion=2.0.0 \
  -p:NativeFixtureDependencyVersion=2.0.0
```

The command, tool, and before-agent-start callback include the selected fixture revision and private dependency assembly version in their observable behavior.
