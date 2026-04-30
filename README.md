MF.SSGI - URP - Screen Space Global Illumination - Raymarched shadows
—-------------

Trailers & tutorial video's: **https://www.youtube.com/watch?v=fPAuykKMD_A&list=PLNDnpD94U4_i-nzHHJzB65QA9ieSs_V1u**
**Documentation: PDF included in release!**

—-------------
Do you want to boost your URP game to the next level? Looking for a realtime GI solution that even runs on Mobile?
Look no further; MF.SSGI is easy to use, highly customizable & runs on all platforms!

—-------------------
MF.SSGI is a Screen Space Global Illumination solution that boosts productivity and visual fidelity. Not only does it look great, but by replacing baked GI and light probes with realtime SSGI, you can save valuable production time.
Thanks to highly optimized HLSL Shadermodel 3 code it runs on old hardware and it doesn’t require Ray Tracing (RTX/RX). This results in wide platform support across all major graphics APIs. This makes MF.SSGI unique to its competitors.
It's also a perfect solution for Unity projects with dynamic environments where light-baking simply isn’t possible. A question often asked is: What if a light-source moves offscreen? Not to worry, thanks to its Reflection-probe-fallback feature, offscreen light-sources are still taken care of.

The artistic settings are completely decoupled from the quality-settings and easy to tweak per Post-process-volume override. This allows you to change settings per scene or area, while platform-specific quality-settings are externalized in Scriptable-objects.
Thanks to the visual-debugger it's easy to test & improve your lighting as it gives clear insight to what is happening under the hood. This helps optimize the quality and speeds up development.

For more information, follow my progress over at the Unity forum here:
**https://forum.unity.com/threads/mf-ssgi-for-urp-realtime-screen-space-global-illumination-directx-11-opengl.1334004/**



—---------------
It supports Deferred and Forward rendering. Various projects have been tested in WebGL, DX11/DX12, IOS/Android OpenGL ES 3.0/Nintendo Switch


MF.SSGI has the following features:
- Raymarching: Contact & casted shadows, originating from each pixel in the screen, not just selective light sources. This produces super soft and realistic occlusion.
- Reflection-probe fallback: If a lightsource moves off-screen, nearby reflection-probes will fill the missing GI. As Reflection-probes can be updated in Runtime, MF.SSGI is the perfect solution to light dynamically changing worlds and scenes.
- Quality presets: Ranging from Photo-mode (singular stills), PC,  all the way to Mobile optimized. Just drag-and-drop a scriptable object and you are done.
- Post-process volume overrides: A ton of options to tinker with, tested and iterated upon by my colleagues at Little Chicken as it is used in actual game productions.
- SSGI-Object components: This allows you to change per-object settings such as:
- Toggles: Omni directional source & Cast shadows
- Sliders: Light receive, Light emit & Shadow receive
- Alpha-clipping: Allows for alpha-clipped objects to be rendered correctly
- Vertex expand: Scale objects virtually so even tiny and thin lightstrips can contribute to the Global Illumination.
- Thickness map rendering: As it's a screen-space effect, there is no way of telling the difference between a pillar and a wall in terms of depth-thickness. Thanks to the Thickness-pass light can travel ‘behind’ props in the foreground.
- Debug window: Allows you to view, debug and optimize individual passes such as Shadows, SSGI, thickness, etc.


DISCLAIMERS:
- The current version does not support VR
- The current version only works for URP. The Build-in renderpipeline is on the roadmap



