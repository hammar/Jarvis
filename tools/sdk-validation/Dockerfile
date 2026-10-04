FROM mcr.microsoft.com/dotnet/sdk:10.0.100@sha256:c7445f141c04f1a6b454181bd098dcfa606c61ba0bd213d0a702489e5bd4cd71
RUN apt-get update && apt-get install -y --no-install-recommends strace procps && rm -rf /var/lib/apt/lists/*
WORKDIR /validation
COPY global.json ./
COPY tools/sdk-validation/SdkValidation.csproj tools/sdk-validation/packages.lock.json tools/sdk-validation/
RUN dotnet restore tools/sdk-validation/SdkValidation.csproj --locked-mode
COPY tools/sdk-validation/*.cs tools/sdk-validation/
RUN dotnet build tools/sdk-validation/SdkValidation.csproj -c Release --no-restore -warnaserror
COPY tools/sdk-validation/check-egress.sh /validation/check-egress.sh
COPY tools/sdk-validation/check-containment.sh /validation/check-containment.sh
ENTRYPOINT ["/bin/bash", "/validation/check-egress.sh"]
