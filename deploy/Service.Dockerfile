FROM mcr.microsoft.com/dotnet/sdk:8.0.428-bookworm-slim AS build
ARG SERVICE
ARG SERVICE_TITLE
WORKDIR /src
COPY building-blocks/ building-blocks/
COPY services/${SERVICE}/ services/${SERVICE}/
RUN dotnet publish services/${SERVICE}/src/Olympiad.${SERVICE_TITLE}.Api/Olympiad.${SERVICE_TITLE}.Api.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:8.0.34-bookworm-slim
ARG SERVICE_TITLE
RUN apt-get update && apt-get install -y --no-install-recommends fonts-dejavu-core libfontconfig1 curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_URLS=http://+:8080 SERVICE_DLL=Olympiad.${SERVICE_TITLE}.Api.dll
USER app
HEALTHCHECK --interval=5s --timeout=3s --retries=24 CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
