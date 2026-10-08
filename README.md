# GeoNex

Aplicativo desktop de geoprocessamento para Windows x64.

## Baixar

Abra **Releases** neste repositório e baixe `GeoNex-...-win-x64.zip`. Extraia o ZIP inteiro para uma pasta e execute `GeoNex.exe`.

O pacote inclui os runtimes .NET e Windows App SDK usados pelo aplicativo. É uma build para Windows x64; não execute o `.exe` diretamente de dentro do ZIP.

## Gerar uma build pelo GitHub

O workflow **Windows build and release** pode ser iniciado em **Actions → Windows build and release → Run workflow**. Essa execução compila o aplicativo e disponibiliza o ZIP como artefato para download na página da execução.

Para publicar uma versão na página **Releases**, envie uma tag começando com `v`. Exemplo:

```powershell
git add GeoNex/GeoNex.csproj README.md .github/workflows/release-windows.yml
git commit -m "Prepare Windows release workflow"
git push origin main
git tag v1.0.0
git push origin v1.0.0
```

O workflow compila a versão marcada e anexa o ZIP e o arquivo `.sha256` ao Release. Se `v1.0.0` já existir, escolha outra versão, como `v1.0.1`.

## Compilar localmente

É necessário Windows, .NET 10 SDK, workload `maui-windows` e Visual Studio Build Tools com ferramentas C++ x64.

```powershell
dotnet workload install maui-windows
dotnet publish GeoNex/GeoNex.csproj -f net10.0-windows10.0.19041.0 -c Release -p:RuntimeIdentifierOverride=win-x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true --self-contained true -p:GeoNexBuildNative=true -o artifacts/GeoNex-win-x64
```
