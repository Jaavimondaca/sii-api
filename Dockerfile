FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Sii.RegistroCompraVenta/Sii.RegistroCompraVenta.csproj", "Sii.RegistroCompraVenta/"]
RUN dotnet restore "Sii.RegistroCompraVenta/Sii.RegistroCompraVenta.csproj"
COPY . .
WORKDIR "/src/Sii.RegistroCompraVenta"
RUN dotnet build "Sii.RegistroCompraVenta.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Sii.RegistroCompraVenta.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Sii.RegistroCompraVenta.dll"]
