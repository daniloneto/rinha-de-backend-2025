# Use the official .NET 9 SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Install Native AOT prerequisites
RUN apt-get update && \
    apt-get install -y clang zlib1g-dev && \
    rm -rf /var/lib/apt/lists/*

# Set working directory
WORKDIR /app

# Copy project file and restore dependencies
COPY PaymentBackend.csproj ./
RUN dotnet restore

# Copy source code
COPY . ./

# Build and publish with Native AOT for linux-x64
RUN dotnet publish -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o out

# Use a Debian-based runtime image for better compatibility
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-bookworm-slim AS runtime

# Install necessary packages for AOT runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
    libicu72 \
    libgcc-s1 \
    libstdc++6 \
    wget \
    curl \
    file && \
    rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN groupadd -g 1000 appuser && \
    useradd -r -u 1000 -g appuser appuser

# Set working directory
WORKDIR /app

# Copy the published app from build stage
COPY --from=build /app/out .

# Ensure the PaymentBackend file is executable
RUN chmod +x /app/PaymentBackend

# Change ownership to non-root user
RUN chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Expose port
EXPOSE 8080

# Set environment variables for performance
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV DOTNET_EnableDiagnostics=0
ENV DOTNET_TieredCompilation=0
ENV DOTNET_ReadyToRun=1

# Run the application
ENTRYPOINT ["./PaymentBackend"]
