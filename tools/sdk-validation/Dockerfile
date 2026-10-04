FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
RUN apt-get update && apt-get install -y --no-install-recommends strace procps && rm -rf /var/lib/apt/lists/*
WORKDIR /validation
COPY global.json ./
COPY tools/sdk-validation/SdkValidation.csproj tools/sdk-validation/packages.lock.json tools/sdk-validation/
RUN dotnet restore tools/sdk-validation/SdkValidation.csproj --locked-mode
COPY tools/sdk-validation/*.cs tools/sdk-validation/
RUN dotnet build tools/sdk-validation/SdkValidation.csproj -c Release --no-restore -warnaserror
COPY tools/sdk-validation/check-egress.sh /validation/check-egress.sh
COPY tools/sdk-validation/check-containment.sh /validation/check-containment.sh
COPY tools/sdk-validation/diagnose-dns.sh /validation/diagnose-dns.sh
ENTRYPOINT ["/bin/bash", "/validation/check-egress.sh"]
