# Initial D Remake

Privater Nachbau (eigene C#-Engine). Plan: [PLAN.md](PLAN.md), Formate: [FORMATS.md](FORMATS.md).

Assets kommen aus der eigenen ISO, nie ins Repo.

```sh
dotnet run --project Touge.Formats.Cli -- list <pfad>/MODEL/HCAR.AFS
dotnet run --project Touge.Formats.Cli -- extract <pfad>/MODEL/HCAR.AFS out/hcar
dotnet test
```
