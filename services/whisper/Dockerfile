FROM python:3.11-slim@sha256:e031123e3d85762b141ad1cbc56452ba69c6e722ebf2f042cc0dc86c47c0d8b3
WORKDIR /app
COPY requirements.txt .
COPY requirements.lock.txt .
RUN pip install --no-cache-dir -r requirements.lock.txt
# Immutable English model; inference never downloads or contacts a hosted API.
RUN python -c "from huggingface_hub import snapshot_download; snapshot_download('Systran/faster-whisper-small.en', revision='d1d751a5f8271d482d14ca55d9e2deeebbae577f', local_dir='/models/small.en', allow_patterns=['config.json','model.bin','tokenizer.json','vocabulary.txt'])"
COPY app.py .
RUN useradd -m whisper
USER whisper
ENV HF_HUB_OFFLINE=1 OMP_NUM_THREADS=4 OPENBLAS_NUM_THREADS=4
EXPOSE 8767
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8767", "--no-access-log"]
