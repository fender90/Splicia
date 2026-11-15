# Face model

`centerface.onnx` is CenterFace (https://github.com/Star-Clouds/CenterFace, MIT License, Copyright (c) 2019 StarClouds),
the face detector Splicia uses to find faces to blur or pixelate. It is the project's `models/onnx/centerface.onnx`
with its input made any size (batch, height and width named instead of fixed, and the weights no longer listed as
graph inputs), the same change the deface tool (https://github.com/ORB-HD/deface, MIT) makes at run time.

`rtmo-s.onnx` is RTMO-s from MMPose (https://github.com/open-mmlab/mmpose, Apache License 2.0, Copyright 2018-2023
OpenMMLab), a multi-person pose model: Splicia finds people's heads from their bodies with it, for the faces the face
detector misses (in profile, turned away, looking down, partly covered). It is
`rtmo-s_8xb32-600e_body7-640x640-dac2bf74_20231211.zip`'s `end2end.onnx` from
https://download.openmmlab.com/mmpose/v1/projects/rtmo/onnx_sdk/, unchanged.

They are installed with ONNX Runtime (https://github.com/microsoft/onnxruntime, MIT License) in `ffmpeg\faces\`.
