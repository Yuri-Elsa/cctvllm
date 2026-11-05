# syntax=docker/dockerfile:1
# CCTV description app: ASP.NET Core + FFmpeg + the YOLO person-tracking environment.
#   docker build -t cctvllm .
# docker-compose.yml wires it to an Ollama container; read the notes there before exposing the port.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY CctvDemo.csproj ./
RUN dotnet restore CctvDemo.csproj
COPY . .
RUN dotnet publish CctvDemo.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# torch comes from the CPU wheel index so the image does not carry the CUDA libraries.
# Pass --build-arg TORCH_INDEX=https://pypi.org/simple to get the default (CUDA) wheels instead.
ARG TORCH_INDEX=https://download.pytorch.org/whl/cpu
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg python3 python3-venv libgl1 libglib2.0-0 \
 && rm -rf /var/lib/apt/lists/*
COPY tracking/requirements.txt /tmp/requirements.txt
RUN python3 -m venv /opt/venv \
 && grep -E '^(torch|torchvision)==' /tmp/requirements.txt > /tmp/torch-requirements.txt \
 && /opt/venv/bin/pip install --no-cache-dir -r /tmp/torch-requirements.txt --extra-index-url "$TORCH_INDEX" \
 && /opt/venv/bin/pip install --no-cache-dir -r /tmp/requirements.txt \
 && rm /tmp/requirements.txt /tmp/torch-requirements.txt

WORKDIR /app
COPY --from=build /app ./
ENV CCTV_YOLO_PYTHON=/opt/venv/bin/python \
    CCTV_DATA_DIR=/data \
    CCTV_URL=http://0.0.0.0:8766 \
    YOLO_CONFIG_DIR=/tmp/Ultralytics
RUN mkdir -p /data && chown "$APP_UID" /data
USER $APP_UID
VOLUME /data
EXPOSE 8766
ENTRYPOINT ["dotnet", "CctvDemo.dll"]