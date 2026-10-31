"""Demucs entry point with lossless PCM output independent of torchaudio backends."""

import wave

import torch
from demucs import separate


def save_pcm(wav, path, samplerate=44100, **kwargs):
    wav = wav.detach().cpu()
    peak = wav.abs().max().item()
    if peak > 0.99:
        wav = wav * (0.99 / peak)
    pcm = (wav * 32767).clamp(-32768, 32767).to(torch.int16).t().contiguous().numpy().tobytes()
    with wave.open(str(path), "wb") as output:
        output.setnchannels(wav.shape[0])
        output.setsampwidth(2)
        output.setframerate(samplerate)
        output.writeframes(pcm)


if __name__ == "__main__":
    separate.save_audio = save_pcm
    separate.main()
