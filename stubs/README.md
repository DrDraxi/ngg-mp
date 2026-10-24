Compile-time stubs of the game's assemblies: Assembly-CSharp, ECM2, UnityEngine.UI, Unity.TextMeshPro and
Unity.InputSystem. The Assembly-CSharp stub declares the types and members documented in docs/gamemap (02..05)
with throwing bodies; the others carry only what those declarations and the plugin need.

They are used only when GameManagedDir has no real Assembly-CSharp.dll (cloud / CI builds). The plugin then
compiles with STUB_GAME defined, references the stubs with Private=false (never copied next to the plugin), and
the packaging scripts refuse such a build. Real builds reference the game's Managed DLLs instead.

Markers in the sources:
  TYPE-GUESS    the member's type (or a type's kind / base class / name) is not documented; most plausible choice.
  ACCESS-GUESS  access is not documented. Public only where the docs show another game class using the member;
                otherwise private, so plugin code has to go through AccessTools and survives either answer.
  SIG-GUESS     the member is documented by name only; parameters / return type / parameter names guessed.
                Declared private (so the plugin reaches it by name through AccessTools, and a wrong guess disables
                a feature instead of breaking the build against the real game) unless the docs show another game
                class calling it.
Members documented as private are private here too: reach them with AccessTools / Traverse / string names.
Optional parameters are never declared: always pass every argument. Never name a type marked "Do not name the
type" (its real name is unknown); reach it through the documented field instead.
