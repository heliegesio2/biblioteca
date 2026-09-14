# docs/operations.md#imagem — multi-stage, runtime não-root, sem SDK na imagem final.
# A mesma imagem serve a API (entrypoint padrão) e o migrator (Job do Helm / serviço do
# Compose sobrescrevem o entrypoint para rodar o bundle de migrations).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore separado do build: a camada de pacotes só invalida quando os .csproj mudam.
COPY Biblioteca.slnx Directory.Build.props Directory.Packages.props ./
COPY src/Biblioteca.Api/*.csproj src/Biblioteca.Api/
RUN dotnet restore src/Biblioteca.Api/Biblioteca.Api.csproj

COPY . .
RUN dotnet publish src/Biblioteca.Api -c Release -o /app --no-restore

# Bundle de migrations (docs/operations.md#migrations): um executável que roda
# `dotnet ef database update` sem precisar do SDK nem do projeto no runtime final — é o
# que o serviço `migrator` do Compose e o `Job` do Helm executam. Framework-dependente
# (não --self-contained): a imagem final já traz o runtime .NET compatível.
FROM build AS bundle
RUN dotnet tool install --global dotnet-ef --version 10.0.12
ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet ef migrations bundle \
    --project src/Biblioteca.Api \
    --configuration Release \
    --output /app/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
COPY --from=bundle /app/efbundle ./efbundle

# Não-root (já definido na imagem base) e porta sem capability privilegiada.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Biblioteca.Api.dll"]
