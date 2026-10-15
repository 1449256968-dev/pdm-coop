# Third-party notice

The recovered project includes Mirror Networking v96.11.3 from the official MirrorNetworking repository:
https://github.com/MirrorNetworking/Mirror/releases/tag/v96.11.3

Mirror is distributed under the MIT License. The complete license is included at `ThirdPartyLicenses/Mirror-LICENSE.txt`.

The project currently includes the Mirror runtime, KCP transport, editor tooling, and related source assets. Upstream Tests and Examples were not imported. The unused Hosting/Edgegap tooling was moved to `RecoveryBackups/Mirror-Hosting-unused/` to avoid a Newtonsoft.Json type conflict in this recovered project.

TextMeshPro shaders and shader includes were restored from Unity's official
`com.unity.textmeshpro` 3.0.6 Essential Resources to replace AssetRipper dummy shaders.
Only those shader sources were copied; existing asset GUIDs and font materials are preserved.
The Unity Companion License is included at `ThirdPartyLicenses/TextMeshPro-3.0.6-LICENSE.md`.
Source archive: https://packages.unity.com/com.unity.textmeshpro/-/com.unity.textmeshpro-3.0.6.tgz

