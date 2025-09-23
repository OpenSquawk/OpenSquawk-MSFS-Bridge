# run it

once
`choco install dotnet-sdk --version=8.0.100`

for building
```
dotnet restore       # zieht NuGet-Pakete
dotnet build         # Debug-Build
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

