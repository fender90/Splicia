# Face model

`centerface.onnx` is CenterFace (https://github.com/Star-Clouds/CenterFace, MIT License, Copyright (c) 2019 StarClouds),
the face detector Splicia uses to find faces to blur or pixelate. It is the project's `models/onnx/centerface.onnx`
with its input made any size (batch, height and width named instead of fixed, and the weights no longer listed as
graph inputs), the same change the deface tool (https://github.com/ORB-HD/deface, MIT) makes at run time.

It is installed with ONNX Runtime (https://github.com/microsoft/onnxruntime, MIT License) in `ffmpeg\faces\`.
