FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled
COPY --from=build /out /app
ENV ASPNETCORE_URLS=http://0.0.0.0:4000
EXPOSE 4000
ENTRYPOINT ["dotnet", "/app/ai-gateway.dll"]
