# Dockerfile
FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH

COPY Source /Source
RUN dotnet publish \
    /Source/LibationCli/LibationCli.csproj \
    --os linux \
    --arch ${TARGETARCH} \
    --configuration Release \
    --output /Source/bin/Publish/Linux-chardonnay \
    -p:PublishProtocol=FileSystem \
    -p:PublishReadyToRun=true \
    -p:SelfContained=true

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS libationd
ARG USER_UID=1001
ARG USER_GID=1001

# Set the character set that will be used for folder and filenames when liberating
ENV LANG=C.UTF-8 LC_ALL=C.UTF-8 LIBATION_FILES_DIR=/config LIBATION_BOOKS_DIR=/data

RUN mkdir -m777 /config /data

COPY --from=build /Source/bin/Publish/Linux-chardonnay /libation

RUN ln -s /libation/LibationCli /usr/local/bin/LibationCli

USER ${USER_UID}:${USER_GID}

EXPOSE 8080
HEALTHCHECK CMD ["/libation/LibationCli","health"]
ENTRYPOINT ["/libation/LibationCli"]
CMD ["serve"]
