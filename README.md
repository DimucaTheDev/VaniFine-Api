# VaniFine Web API

A lightweight ASP.NET Core Web API that tracks Minecraft releases and maps game versions to their corresponding resource pack major versions (`pack_version`).

## How It Works

1. Periodically fetches the official Mojang version manifest in the background (every 24 hours).
2. Downloads new client `.jar` files and extracts `pack_version.resource` from `version.json`.
3. Caches the mapped data into a local `versions.json` file and serves it via HTTP API `/map`.

## Quick Start (Docker)

```bash
git clone https://github.com/DimucaTheDev/VaniFine-Api.git
cd VaniFine-Api
docker compose up -d
```
## Alt: using .NET
1. Install .NET 10 SDK
2. ```bash
   git clone https://github.com/DimucaTheDev/VaniFine-Api.git
   cd VaniFine-Api
   dotnet run
   ```
