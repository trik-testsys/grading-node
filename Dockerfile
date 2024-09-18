FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
RUN echo 'APT::Install-Recommends "0";' > /etc/apt/apt.conf.d/99norecommends \
    && apt-get -y update \
    && apt-get -y install \
    curl \
    && apt-get autoremove -y \
    && apt-get clean -y
RUN curl -fsSL https://get.docker.com -o get-docker.sh
RUN chmod +x ./get-docker.sh
RUN ./get-docker.sh > /dev/null
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet build -c Release --property:PublishDir=/app/build

FROM build AS publish
RUN dotnet publish "TestSys.Trik.GradingNode.Runner/TestSys.Trik.GradingNode.Runner.csproj" -c Release --property:PublishDir=/app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TestSys.Trik.GradingNode.Runner.dll"]