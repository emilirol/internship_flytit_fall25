# internship_flytit_fall25
En fullstack RAG‑chatbot for nettsider bygget med .NET 8 (C#) backend, Svelte frontend og Elasticsearch for søk.
Denne README forklarer hvordan du kjører prosjektet fra start til slutt – enten med Docker (anbefalt) eller lokalt for utvikling.

## Teknologistack
- Backend: .NET 8, ASP.NET Core, Microsoft Semantic Kernel (chat), Elastic.Clients.Elasticsearch
- Frontend: Svelte (Vite)
- Søk: Elasticsearch 8.x
- Containere: Docker & Docker Compose
- OCR: Tesseract (lokalt) eller Azure AI Document Intelligence

## Forutsetninger
- Docker og Docker Compose
- .NET 8 SDK, Node.js 18+ og pnpm/npm (Valgfritt for lokal utvikling)

## .env for backend
Opprett en fil .env der Program.cs leses fra:
```
# LLM
OPENAI_API_KEY

# Elasticsearch
ES_ENDPOINT
ES_USER
ES_PASS
ES_INDEX_NAME

# Indexering
DEFAULT_FOLDER (der pdfer hentes fra)
INDEX_PATTERN="*.pdf,*.docx,*.txt"
INDEX_RECURSIVE=true
INDEX_RENDER_PAGES=true
INDEX_IMAGE_CAPTIONS=true
CAPTION_MODE=always

# (Valgfritt) CORS/Origins for frontend
ALLOWED_HOSTS
```

## Start
1. 
```
git clone <repo-url>
cd flytIT-chatbot
```
2. Sett opp en .env og sjekk docker-compose.yml at portene ikke kolliderer
3. Bygg og start Docker 
```
cd backend
docker compose up --build
```
4. Sjekk Elasticsearch
```
docker compose exec elasticsearch curl -s http://localhost:9200
docker compose exec elasticsearch curl -s http://localhost:9200/_cat/health?v
```
5. Opprett index om dette ikke er skjedd automatisk
`dotnet run -- --create-index`
6. Indexker dokumenter og HTML fra nettstedet
```
dotnet run -- --cmd index --folder "<folder for filer>"
dotnet run -- --cmd siteindex
```
7. Åpne frontend på http://localhost:5173 (om denne er eksponert fra compose)

## Utvikling uten Docker
- Backend
```
cd backend
dotnet restore
dotnet run
```
Backend skal da starte på localhost:5000
- Frontend
```
cd frontend
npm install
npm run dev
```
Frontend starter da "by default" på localhost:5173

## API-endpunkter (backend)
- POST /chat
    - Body: { "message": "..", "site": "intranett" }
    - Response: { reply: string, sources: [{ title, url }] }
- SignalR Hub: /chatHub (for streaming i frontend)

## Script som legges inn på kundes website
Denne kodesnippeten legges inn i hos nettstedet/ kunden
<script
  src="https://cdn.dittdomene.no/internship_flytit_fall25/v1/embed.js" 
  data-internship_flytit_fall25
  data-api-base="https://chat.dittdomene.no"
  data-site-id="acme-no"
  data-theme="auto"
  data-position="bottom-right"
  data-language="nb"
  defer
></script>

