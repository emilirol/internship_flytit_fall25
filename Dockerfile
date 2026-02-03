FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN set -eux; \
    PROJECT_FILE="$(find . -name '*.csproj' | head -n1)"; \
    echo "Using project file: $PROJECT_FILE"; \
    dotnet restore "$PROJECT_FILE"; \
    dotnet publish "$PROJECT_FILE" -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out ./
RUN printf '#!/bin/sh\nset -e\nexec dotnet $(ls *.dll | head -n1)\n' > /app/run.sh && chmod +x /app/run.sh
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["/app/run.sh"]
