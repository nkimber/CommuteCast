FROM python:3.11-slim@sha256:e031123e3d85762b141ad1cbc56452ba69c6e722ebf2f042cc0dc86c47c0d8b3
RUN apt-get update && apt-get install -y --no-install-recommends libsndfile1 espeak-ng curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY requirements.txt .
COPY requirements.lock.txt .
RUN pip install --no-cache-dir -r requirements.lock.txt
COPY --from=commutecast_models kokoro/kokoro-v1.0.onnx kokoro/voices-v1.0.bin /models/kokoro/
COPY --from=commutecast_models piper/en_US-amy-medium.onnx piper/en_US-amy-medium.onnx.json piper/en_US-bryce-medium.onnx piper/en_US-bryce-medium.onnx.json piper/en_US-joe-medium.onnx piper/en_US-joe-medium.onnx.json piper/en_US-lessac-medium.onnx piper/en_US-lessac-medium.onnx.json piper/en_US-ljspeech-medium.onnx piper/en_US-ljspeech-medium.onnx.json piper/en_GB-alan-medium.onnx piper/en_GB-alan-medium.onnx.json piper/en_GB-alba-medium.onnx piper/en_GB-alba-medium.onnx.json piper/en_GB-jenny_dioco-medium.onnx piper/en_GB-jenny_dioco-medium.onnx.json /models/piper/
COPY app.py diagnostics.py voice_library.py .
COPY model-checksums.txt /models/model-checksums.txt
RUN cd /models && sha256sum -c model-checksums.txt
RUN useradd -m speech && chown -R speech:speech /app /models
USER speech
ENV OMP_NUM_THREADS=2 OPENBLAS_NUM_THREADS=2
EXPOSE 8765
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8765", "--no-access-log"]
