import copy
import hashlib
import json
import wave

import pytest

from learnmore_api import local_evidence, qwen_evidence


def fixture(tmp_path):
    evidence_id = "a" * 32
    bundle = tmp_path / evidence_id
    bundle.mkdir()
    for name in ("source.wav", "vocals.wav"):
        with wave.open(str(bundle / name), "wb") as audio:
            audio.setparams((1, 2, 100, 0, "NONE", "not compressed"))
            audio.writeframes(b"\x00\x00" * 500)
    vocals = bundle / "vocals.wav"
    raw = {"models": qwen_evidence.MODELS, "backend": "transformers-cuda-bfloat16",
           "verificationStatus": "unreviewed", "results": [{
               "file": "vocals.wav", "fileSha256": local_evidence.digest(vocals.read_bytes()),
               "language": "English", "text": "Stand by",
               "timeStamps": {"items": [{"text": "Stand", "start_time": 2, "end_time": 2.4},
                                         {"text": "by", "start_time": 2.4, "end_time": 2.7}]}}]}
    (bundle / "raw.json").write_text(json.dumps(raw))
    lyrics = [{"lyricId": 1, "japanese": "Stand by", "currentStart": 2.1}]
    manifest = {"version": 1, "evidenceId": evidence_id, "snapshotHash": "A" * 64,
                "songUid": "fixture-song", "youtubeUrl": "https://www.youtube.com/watch?v=abcdefghijk",
                "language": "en", "lyrics": lyrics}
    for name in ("source", "vocals", "raw"):
        file = name + (".json" if name == "raw" else ".wav")
        manifest[name] = {"file": file, "sha256": local_evidence.digest((bundle / file).read_bytes())}
    register(tmp_path, bundle, manifest)
    args = [str(tmp_path), evidence_id, manifest["snapshotHash"], manifest["songUid"],
            manifest["youtubeUrl"], manifest["language"], lyrics]
    return bundle, manifest, raw, args


def register(root, bundle, manifest):
    content = json.dumps(manifest).encode()
    (bundle / "manifest.json").write_bytes(content)
    (root / "registry.json").write_text(json.dumps({manifest["evidenceId"]: local_evidence.digest(content)}))
    (root / "registry.json").chmod(0o600)


def test_server_rebuilds_lines_from_words_without_trusting_client_scores(tmp_path):
    bundle, manifest, raw, args = fixture(tmp_path)
    manifest["accepted"] = True
    manifest["alignedLines"] = [{"start": 0, "score": 1}]
    register(tmp_path, bundle, manifest)
    result = local_evidence.verify(*args)
    assert result["matchedCount"] == 1
    assert result["alignedLines"][0]["start"] == 2
    assert result["language"] == "en"
    assert "accepted" not in result


@pytest.mark.parametrize("changed", ["snapshot", "song", "video", "language", "lyrics", "manifest", "audio", "unregistered", "zero", "duplicate"])
def test_changed_missing_or_invalid_proofs_are_rejected(tmp_path, changed):
    bundle, manifest, raw, args = fixture(tmp_path)
    if changed == "snapshot": args[2] = "B" * 64
    if changed == "song": args[3] = "other"
    if changed == "video": args[4] += "other"
    if changed == "language": args[5] = "auto"
    if changed == "lyrics": args[6] = [dict(args[6][0], japanese="Other")]
    if changed == "manifest": (bundle / "manifest.json").write_text("{}")
    if changed == "audio": (bundle / "source.wav").write_bytes(b"other")
    if changed == "unregistered": (tmp_path / "registry.json").write_text("{}")
    if changed == "zero":
        raw["results"][0]["timeStamps"]["items"][0]["end_time"] = 2
        (bundle / "raw.json").write_text(json.dumps(raw))
        manifest["raw"]["sha256"] = local_evidence.digest((bundle / "raw.json").read_bytes())
        register(tmp_path, bundle, manifest)
    if changed == "duplicate":
        args[6] = [args[6][0], args[6][0]]
        manifest["lyrics"] = args[6]
        register(tmp_path, bundle, manifest)
    with pytest.raises(ValueError):
        local_evidence.verify(*args)


def test_unknown_text_and_repeated_phrases_remain_unreviewed(tmp_path):
    bundle, manifest, raw, args = fixture(tmp_path)
    items = raw["results"][0]["timeStamps"]["items"]
    raw["results"][0]["timeStamps"]["items"] += [dict(word, start_time=word["start_time"] + 1,
                                                              end_time=word["end_time"] + 1) for word in copy.deepcopy(items)]
    raw["results"][0]["text"] = "Stand by Stand by"
    (bundle / "raw.json").write_text(json.dumps(raw))
    manifest["raw"]["sha256"] = local_evidence.digest((bundle / "raw.json").read_bytes())
    register(tmp_path, bundle, manifest)
    result = local_evidence.verify(*args)
    assert result["matchedCount"] == 0


def test_unconfigured_registry_fails_closed():
    with pytest.raises(ValueError, match="not_configured"):
        local_evidence.verify("", "a" * 32, "A" * 64, "song", "url", "ja", [])


@pytest.mark.parametrize("target", ["registry", "bundle", "raw"])
def test_symlinks_are_not_trusted(tmp_path, target):
    bundle, _, _, args = fixture(tmp_path)
    path = tmp_path / "registry.json" if target == "registry" else bundle if target == "bundle" else bundle / "raw.json"
    destination = path.with_name(path.name + "-original")
    path.rename(destination)
    path.symlink_to(destination)
    with pytest.raises(ValueError):
        local_evidence.verify(*args)


def test_group_writable_registry_is_rejected(tmp_path):
    _, _, _, args = fixture(tmp_path)
    (tmp_path / "registry.json").chmod(0o660)
    with pytest.raises(ValueError, match="registry"):
        local_evidence.verify(*args)


def test_authenticated_endpoint_revalidates_without_inference(tmp_path, monkeypatch):
    from fastapi.testclient import TestClient
    from learnmore_api import main

    _, _, _, args = fixture(tmp_path)
    monkeypatch.setenv("LEARNMORE_API_TOKEN", "fixture-token")
    monkeypatch.setenv("LOCAL_EVIDENCE_ROOT", str(tmp_path))
    monkeypatch.setattr(main, "get_whisper_model", lambda *_: pytest.fail("must not infer"))
    payload = dict(evidenceId=args[1], snapshotHash=args[2], songUid=args[3],
                   youtubeUrl=args[4], language=args[5], lyrics=args[6])
    with TestClient(main.app) as client:
        assert client.post("/v1/local-alignment-evidence/verify", json=payload).status_code == 401
        response = client.post("/v1/local-alignment-evidence/verify", json=payload,
                               headers={"Authorization": "Bearer fixture-token"})
        assert response.status_code == 200
        assert response.json()["matchedCount"] == 1
        payload["snapshotHash"] = "B" * 64
        response = client.post("/v1/local-alignment-evidence/verify", json=payload,
                               headers={"Authorization": "Bearer fixture-token"})
        assert response.status_code == 409
        assert response.json()["detail"] == "evidence_snapshot_mismatch"
