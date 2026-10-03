# Bundled OCR models

These two files complete the PP-OCRv5 pipeline that [RapidOcrNet](https://github.com/BobLd/RapidOcrNet)
ships (mobile detector + textline-orientation classifier) with **Chinese + English recognition**.
They are copied to `models/v5/` next to the app binary at build time.

| File | Purpose | Size |
|---|---|---|
| `ch_PP-OCRv5_rec_mobile.onnx` | Chinese/English recognition (mobile tier, fast) | 16,631,306 B |
| `ppocrv5_dict.txt` | Recognizer character set (18k+ chars incl. full-width punctuation) | 74,012 B |

Source: [RapidOCR model zoo](https://github.com/RapidAI/RapidOCR) (ModelScope mirror
`RapidAI/RapidOCR` tag `v3.9.2`), originally exported from
[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) PP-OCRv5. Both projects and their models are
licensed under Apache-2.0.

The heavier PP-OCRv5 **server** models used by the "accurate" tier are *not* vendored; they are
downloaded on demand into `%LOCALAPPDATA%\Modern-ScreenShot\models\ocr`
(see `OcrModelDownloader` in `ModernScreenShot.Core/Ocr`).
