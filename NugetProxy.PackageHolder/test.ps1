dotnet nuget locals all -c

Get-ChildItem -Path ../NugetProxy/nuget-cache -Recurse | Remove-Item -Recurse -Force -Confirm:$false

dotnet restore --no-cache NugetProxy.PackageHolder.csproj