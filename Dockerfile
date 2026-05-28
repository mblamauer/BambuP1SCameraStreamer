FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["BambuStreamer.csproj", "./"]
RUN dotnet restore "BambuStreamer.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "./BambuStreamer.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./BambuStreamer.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS dotnet-build
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BambuStreamer.dll"]


FROM debian:12 as download-go2rtc

RUN apt update && apt install -y curl  && rm -rf /var/lib/apt/lists/*

WORKDIR /app
RUN curl -LJ "https://github.com/AlexxIT/go2rtc/releases/download/v1.6.2/go2rtc_linux_$TARGETARCH" > go2rtc
RUN chmod +x go2rtc


FROM debian:12

WORKDIR /app
COPY --from=dotnet-build /app/publish/BambuStreamer /app/

RUN echo \
'streams:\n'\
'   p1s: "exec: ./BambuStreamer --ip ${PRINTER_ADDRESS} --code ${PRINTER_ACCESS_CODE}"\n'\
'log:\n'\
'  level: debug\n'\
'api:\n'\
'  origin: "*"\n'\
> /app/go2rtc.yaml 

COPY --from=download-go2rtc /app/go2rtc /app/
RUN chmod +x go2rtc

WORKDIR /app

CMD [ "./go2rtc" ]
