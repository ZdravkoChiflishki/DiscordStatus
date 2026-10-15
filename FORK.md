# DiscordStatus maintenance fork

This is a GPL-3.0 maintenance fork of [TianxSky/DiscordStatus](https://github.com/TianxSky/DiscordStatus), based on upstream commit `2fbe5acb2801a976191da51633154ab64730204b` / release `v3.2.2`. Original attribution and license are retained. See Git history for the modified files and authors.

## Scope of this candidate

Production stacks showed Discord.Net's webhook URL constructor performing an HTTP GET and failing outside `UpdateEmbed`'s catch. That exception propagated into an async-void timer handler. A failure talking to Discord must not terminate CS2.

This fork adds a small, secret-safe exception boundary at periodic/manual updates, request commands, map/scoreboard notifications, and the best-effort offline update. The timer callback is no longer async-void. Failed request commands do not announce successful delivery. Map notifications and the offline update run off the game thread because constructing a webhook client can block on HTTP. Constructor failures and completed operations dispose their REST transports/clients. Network errors are logged by exception type, not exception message or URL.

It does **not** redesign initialization, update scheduling or shutdown. Existing timer overlap, shared mutable state, startup failure behavior and online/offline write ordering remain upstream limitations; no automatic delivery retry/backoff or guarantee of a final offline message is added. A failed startup may still need a reload. This change does not diagnose or repair the network conditions that caused the HTTP failures. In-flight work can outlive unload; do not treat this candidate as a lifecycle/concurrency fix.

## Build and tests

Use a .NET 8 SDK (selected by `global.json`) and Python 3. The plugin keeps upstream's `net7.0` target, CounterStrikeSharp.API 1.0.139 build reference and Discord.Net 3.13.0 dependency baseline. The test harness targets `net8.0`; it is not packaged into the plugin.

```sh
dotnet restore Tests/DiscordStatus.RegressionTests.csproj --locked-mode
dotnet run --project Tests -c Release --no-restore
python3 -m unittest discover -s scripts -p 'test_*.py' -v
dotnet build Source/DiscordStatus.csproj -c Release --no-restore -o .artifacts/plugin
# Packaging requires a clean, committed checkout for matching corresponding source.
python3 scripts/package.py
```

The C# harness injects failures into real Discord.Net constructors and HTTP sends without making any network connections. It covers synchronous/asynchronous failures, later status recovery, all six webhook operations, REST disposal, secret-safe logging, the actual console request command, and timer survival in a subprocess. Upstream nullable/configuration warnings remain. Tests do not prove native CounterStrikeSharp loading, real Discord delivery, or long-running lifecycle safety.

## Independent release artifacts

GitHub Actions builds/tests pull requests and main. A version tag matching `Source/DiscordStatus.csproj` and `Source/Manifests.cs` additionally publishes a **prerelease**, never a game-server deployment.

Each release contains:
- `DiscordStatus-v<version>.zip`: the familiar `release/addons/counterstrikesharp/plugins/DiscordStatus/` layout.
- `DiscordStatus-v<version>-source.zip`: matching corresponding source, license, build scripts, dependency lockfiles and tests from the exact tagged commit.
- `SHA256SUMS`: checksums for both archives.

The packaging script downloads the original `v3.2.2/release.zip` and verifies SHA-256 `ef9938ff1d47df4ac16f3a55e71697c6f5bd2c13b53be2d04358104208778a47`. It replaces **only DiscordStatus.dll and DiscordStatus.pdb**, preserves the released dependency DLLs and dependency metadata, and adds license/fork/build provenance. It does not ship the CounterStrikeSharp host API or SDK. Dependency upgrades are deliberately a separate change.

The CS2 platform can consume the resulting ZIP by exact release URL and SHA-256 through its existing plugin installer. It does not need this source tree or a C# build stage. Changing the platform dependency and deploying it are separate, explicitly authorized steps.

Before promotion to a stable release, verify native plugin loading and genuine Discord updates in a controlled CS2 environment. Do not induce Discord/network outages on public game servers. The candidate is not deployed or production-accepted merely because its build is green.
