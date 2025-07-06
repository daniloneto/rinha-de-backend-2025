# Backend Service

This project implements the payment API for the Rinha de Backend challenge using .NET 9 Minimal API and is ready for Native AOT publishing.

## Building locally

1. Install the **.NET 9 SDK**.
2. Restore and publish the application for linux-x64 using Native AOT:

   ```bash
   dotnet publish -c Release -p:PublishAot=true --self-contained -r linux-x64
   ```

The output binary will be available in `bin/Release/net9.0/linux-x64/publish`.

## Running with Docker

You can also build the Docker image which already performs the AOT publish step:

```bash
docker build -t backend .
```

Then run it with the required environment variables:

```bash
docker run -e PAYMENT_PROCESSOR_URL_DEFAULT=<url> \
           -e PAYMENT_PROCESSOR_URL_FALLBACK=<url> \
           -p 9999:9999 backend
```

The `docker-compose.yml` in the repository shows a complete setup.
