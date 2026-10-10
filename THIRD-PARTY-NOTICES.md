# Third-party notices

## Ultracraft

Parts of the ULTRAKILL plugin (`src/UkBridge`) are adapted from Ultracraft's UltraBridge
(https://github.com/alfr0762/ultracraft): stripping the Sandbox down to V1, the frame hand-over through a
memory-mapped file, input through virtual Input System devices, enemy stand-ins, the depth-only occluder, conduction
on stand-ins, the gasoline report, and the source of V1's body. The places are marked in the code. Ultracraft is
distributed under the MIT License:

```
MIT License

Copyright (c) 2026 Ultracraft contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Not included

No files from ULTRAKILL or Risk of Rain 2 are part of this repository. BepInEx, R2API, RoR2BepInExPack, HookGenPatcher,
FixPluginTypesSerialization and Risk of Options are downloaded from their own sources by `tools\Setup-Profiles.ps1`
and are covered by their own licences.
