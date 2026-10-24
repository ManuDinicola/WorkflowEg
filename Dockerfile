# syntax=docker/dockerfile:1
# Use the 11.0 tags once .NET 11 is GA; until then the preview/RC tags (see DECISIONS.md).
ARG DOTNET_TAG=11.0-preview

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig BannedSymbols.txt Flowd.slnx ./
COPY src/ src/
COPY db/ db/
RUN dotnet publish src/Flowd.Host/Flowd.Host.csproj -c Release -o /app --nologo -v:q

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG} AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "flowd.dll"]
CMD ["serve"]
