#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

#Depending on the operating system of the host machines(s) that will build or run the containers, the image specified in the FROM statement may need to be changed.
#For more information, please see https://aka.ms/containercompat

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["B2CVerifiedID.csproj", ""]
RUN dotnet restore "./B2CVerifiedID.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "B2CVerifiedID.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "B2CVerifiedID.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "B2CVerifiedID.dll"]

### build
# docker build -t b2cverifiedid:v1.0 .

### run Windows - remember to update your appSettings.json file
# docker run --rm -it -p 5000:80 b2cverifiedid:v1.0

### browse
# http://localhost:5000