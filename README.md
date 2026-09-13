# Silksong Rando Logic Manager

Silksong Rando Logic Manager is a local, single-user tool for documenting
Silksong rooms and the logic used to traverse them. It manages rooms, exits,
subrooms, directed connections, checks, requirements, notes, map context, and
verification state in a SQLite database.

This project was never initially intended to be released into the wild. It is a
fully AI-generated program designed for the sole purpose of being a throwaway
tool for managing the Silksong logic needed to help create the Silksong
Randomizer Archipelago mod (the Batsong implementation).

As the number of contributors working on map logic increased, it became clear
that a unified solution was probably the best way to manage the problem. So here
we are: releasing the source for a janky AI app into the wild.

Massive thank you to **RainingChain** and **IdoManti** for allowing us to generate
room annotations using the 25%-resolution image of their amazing high-detail
Silksong map.

[You can find the interactive version of their map and their incredible work here.](https://scripterswar.com/silksong/map)

## Download

Self-contained versions for Windows x64, mainstream glibc Linux x64, and Apple
Silicon macOS are available from the repository's GitHub Releases page.

These packages do not require an installed .NET runtime or SDK.

## Build and run from source

Source builds require the .NET 10 SDK. After cloning the repository, run:

```powershell
dotnet restore "Silksong Rando Logic Manager.slnx"
dotnet build "Silksong Rando Logic Manager.slnx" -c Release
dotnet run --project "Silksong Rando Logic Manager/Silksong Rando Logic Manager/Silksong Rando Logic Manager.csproj"
```

The application runs as a local server-hosted Blazor app. Follow the URL shown
in the terminal and keep that process running while using the application.

## Data and upgrades

The source repository contains a snapshot of the working SQLite database at:

```text
Silksong Rando Logic Manager/Silksong Rando Logic Manager/Data/silksong-rando-logic.db
```

The running application treats the database and generated scene assets in its
`data` directory as live user data.

Back up the complete `data` directory before upgrades or major imports. When
upgrading a portable installation, retain the existing `data` directory while
replacing the application and runtime files.

## Documentation

- [Application guide](readme/README.md)
- [Logic guide](readme/LOGIC.md)

## License

Except for separately attributed map imagery, this project is licensed under the
MIT License.
