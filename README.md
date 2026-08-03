<h1 align="center">Jellyfin TVHeadend Plugin</h1>
<h3 align="center">Part of the <a href="https://jellyfin.org">Jellyfin Project</a></h3>

<p align="center">
<img alt="Plugin Banner" src="https://raw.githubusercontent.com/jellyfin/jellyfin-ux/master/plugins/SVG/jellyfin-plugin-tvheadend.svg?sanitize=true"/>
<br/>
<br/>
<a href="https://github.com/jellyfin/jellyfin-plugin-tvheadend/actions/workflows/build.yaml">
<img alt="GitHub Workflow Status" src="https://img.shields.io/github/actions/workflow/status/jellyfin/jellyfin-plugin-tvheadend/build.yaml?branch=master"/>
</a>
<a href="https://github.com/jellyfin/jellyfin-plugin-tvheadend">
<img alt="MIT License" src="https://img.shields.io/github/license/jellyfin/jellyfin-plugin-tvheadend.svg"/>
</a>
<a href="https://github.com/jellyfin/jellyfin-plugin-tvheadend/releases">
<img alt="Current Release" src="https://img.shields.io/github/release/jellyfin/jellyfin-plugin-tvheadend.svg"/>
</a>
</p>

## About

This plugin allows you to manage TVHeadend from Jellyfin.

## Installation

[See the official documentation for install instructions](https://jellyfin.org/docs/general/server/plugins/index.html#installing).

## Build

1. To build this plugin you will need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

2. Build the plugin with the following command:

  ```sh
  dotnet publish TVHeadEnd/TVHeadEnd.csproj --configuration Release --output bin
  ```

3. Place `bin/TVHeadEnd.dll` in the `plugins/tvheadend` folder (you might need to create the folders) of your Jellyfin install.

## Development

The build enforces the shared Jellyfin analyzer set (StyleCop, .NET code analysis, SerilogAnalyzer and MultithreadingAnalyzer) with `TreatWarningsAsErrors`, so warnings fail the build.
Before pushing, check formatting and analyzer compliance:

```sh
dotnet format TVHeadEnd.slnx --verify-no-changes
dotnet build TVHeadEnd.slnx -c Release
```

`dotnet format TVHeadEnd.slnx` (without the flag) applies the fixes it can automatically.

## Releasing

To release the plugin we recommend [JPRM](https://github.com/oddstr13/jellyfin-plugin-repository-manager) that will build and package the plugin.
For additional context and for how to add the packaged plugin zip to a plugin manifest see the [JPRM documentation](https://github.com/oddstr13/jellyfin-plugin-repository-manager) for more info.

## Contributing

We welcome all contributions and pull requests! If you have a larger feature in mind please open an issue so we can discuss the implementation before you start.
In general refer to our [contributing guidelines](https://github.com/jellyfin/.github/blob/master/CONTRIBUTING.md) for further information.

## Licence

This plugins code and packages are distributed under the MIT License. See [LICENSE](./LICENSE) for more information.
