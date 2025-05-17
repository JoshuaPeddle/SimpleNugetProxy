# This is used to test the proxy. 

# First clear all nuget caches
dotnet nuget locals all -c

# Then clear the Proxy Servers cache
Get-ChildItem -Path ../NugetProxy/nuget-cache -Recurse | Remove-Item -Recurse -Force -Confirm:$false

# Now do a restore
dotnet restore --no-cache NugetProxy.PackageHolder.csproj 