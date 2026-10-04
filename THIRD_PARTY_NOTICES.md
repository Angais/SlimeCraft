# Third-party notices

SlimeCraft's own source code is licensed under the MIT license (see `LICENSE`). The component below is third-party
code that keeps its original license.

## puff (zlib contrib/puff) - `SlimeCraft/src/Core/Assets/Inflater.cs`

`Inflater.cs` is a C# translation of `puff.c`, Mark Adler's small reference DEFLATE decoder from the zlib
distribution (`contrib/puff`). It was translated to C# and modified for SlimeCraft (array-based input and output,
exceptions instead of error codes, different structure and names), so it is an **altered version**, not the
original software. It is used only when the runtime's `DeflateStream` is not available.

```
puff.c
Copyright (C) 2002-2013 Mark Adler, all rights reserved

This software is provided 'as-is', without any express or implied
warranty.  In no event will the author be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.

Mark Adler    madler@alumni.caltech.edu
```

## Not included

SlimeCraft does not include or redistribute BepInEx, HarmonyX, Unity, Slime Rancher or Minecraft files. They are
referenced at build time from the user's own installations and, for Minecraft content, read at runtime from the
player's own Minecraft Java Edition install.
