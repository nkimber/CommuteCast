"""Optional CPU comparison of predownloaded expressive models; separate from the app."""
import argparse
import hashlib
import importlib.metadata
import json
import os
import random
from pathlib import Path
import time

# Set before importing any Hugging Face model code. Provision weights separately.
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"

SAMPLE = ("I thought we had the answer. Then one small detail changed everything. Really? "
          "Yes, and that is the interesting part. Imagine a quiet street becoming a busy station: "
          "people arriving, doors opening, and a city finding a new rhythm. What happens next? "
          "Let's slow down and follow the evidence.")
INSTRUCTION = "Speak warmly and conversationally, with natural questions and thoughtful pauses. Keep the same speaker identity."


def validate_qwen(folder):
    config = json.loads((folder / "config.json").read_text(encoding="utf-8"))
    if config.get("tts_model_type") != "custom_voice" or config.get("tts_model_size") != "1b7":
        raise ValueError("This comparison needs Qwen3-TTS 1.7B CustomVoice; Base and 0.6B do not provide this instruction control.")


def synthesize_qwen(model, speaker, expressive):
    waves, rate = model.generate_custom_voice(text=SAMPLE, language="English", speaker=speaker,
                                              instruct=INSTRUCTION if expressive else "")
    return waves[0], rate


def synthesize_nano(model, expressive):
    text = SAMPLE.replace("Really?", "Really? [chuckle]") if expressive else SAMPLE
    # Nano has no CFG/exaggeration control; reference conditioning is cached once.
    wave = model.generate(text, temperature=.8, top_p=.95, top_k=1000, repetition_penalty=1.2)
    return wave.detach().cpu().numpy().reshape(-1), model.sr


def model_inventory(folder):
    result = []
    for path in sorted(p for p in folder.rglob("*") if p.is_file()):
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        result.append({"file": path.relative_to(folder).as_posix(), "sha256": digest.hexdigest()})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("engine", choices=["qwen", "chatterbox-nano"])
    parser.add_argument("model_directory", type=Path)
    parser.add_argument("output_directory", type=Path)
    parser.add_argument("--speaker", default="Ryan", help="Qwen's fixed preset speaker")
    parser.add_argument("--reference", type=Path, help="Nano's clean, single-speaker local WAV, longer than five seconds")
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()
    if not args.model_directory.is_dir() or args.output_directory.exists() or not 1 <= args.threads <= 16:
        parser.error("Supply an existing model directory, a new output folder and 1–16 CPU threads.")
    if args.engine == "qwen":
        validate_qwen(args.model_directory)
    elif args.reference is None or not args.reference.is_file():
        parser.error("Nano needs an explicit local reference WAV.")

    import numpy as np
    import soundfile as sf
    import torch
    torch.set_num_threads(args.threads)
    torch.set_num_interop_threads(1)
    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    random.seed(args.seed)
    reference_hash = None
    if args.engine == "chatterbox-nano":
        reference, reference_rate = sf.read(args.reference)
        if reference.ndim != 1 or len(reference) / reference_rate <= 5 or not np.isfinite(reference).all():
            parser.error("Use a finite, mono reference WAV longer than five seconds.")
        reference_hash = hashlib.sha256(args.reference.read_bytes()).hexdigest()
    args.output_directory.mkdir(parents=True, exist_ok=False)
    load_start = time.perf_counter()
    if args.engine == "qwen":
        from qwen_tts import Qwen3TTSModel
        model = Qwen3TTSModel.from_pretrained(str(args.model_directory.resolve()), device_map="cpu",
                                            dtype=torch.float32, attn_implementation="eager", local_files_only=True)
        if args.speaker.lower() not in model.get_supported_speakers():
            raise ValueError("Choose an installed Qwen preset speaker.")
        generate = lambda expressive: synthesize_qwen(model, args.speaker, expressive)
        package = "qwen-tts"
    else:
        from chatterbox.tts_turbo import ChatterboxTurboTTS
        model = ChatterboxTurboTTS.from_local(args.model_directory.resolve(), device="cpu", nano=True)
        model.prepare_conditionals(str(args.reference.resolve()))
        generate = lambda expressive: synthesize_nano(model, expressive)
        package = "chatterbox-tts"
    load_seconds = time.perf_counter() - load_start
    report = {"engine": args.engine, "device": "cpu", "threads": args.threads, "seed": args.seed,
              "packageVersion": importlib.metadata.version(package), "torchVersion": torch.__version__,
              "speaker": args.speaker if args.engine == "qwen" else "fixed reference",
              "referenceSha256": reference_hash, "models": model_inventory(args.model_directory),
              "loadSeconds": load_seconds, "instruction": INSTRUCTION if args.engine == "qwen" else None,
              "listeningApproval": "pending", "results": []}
    for name, expressive in [("neutral", False), ("expressive", True), ("expressive-repeat", True)]:
        started = time.perf_counter()
        samples, rate = generate(expressive)
        seconds = time.perf_counter() - started
        samples = np.asarray(samples).reshape(-1)
        if len(samples) == 0 or not np.isfinite(samples).all() or np.max(np.abs(samples)) == 0:
            raise ValueError("The candidate returned empty, silent or nonfinite audio.")
        # Float WAV retains the unnormalized output for accurate later comparison.
        target = args.output_directory / (name + ".wav")
        sf.write(target, samples, rate, subtype="FLOAT")
        report["results"].append({"name": name, "file": target.name, "text": SAMPLE.replace("Really?", "Really? [chuckle]") if args.engine == "chatterbox-nano" and expressive else SAMPLE,
                                  "durationSeconds": len(samples) / rate, "synthesisSeconds": seconds,
                                  "realTimeFactor": seconds / (len(samples) / rate),
                                  "peak": float(np.max(np.abs(samples))), "sha256": hashlib.sha256(target.read_bytes()).hexdigest()})
        (args.output_directory / "comparison.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"{name}: {len(samples) / rate:.1f}s audio in {seconds:.1f}s; {target}", flush=True)


if __name__ == "__main__":
    main()
