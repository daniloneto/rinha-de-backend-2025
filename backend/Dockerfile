FROM mcr.microsoft.com/dotnet/nightly/sdk:9.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app /p:PublishAot=true --self-contained -r linux-x64

FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:9.0-alpine
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./backend"]
