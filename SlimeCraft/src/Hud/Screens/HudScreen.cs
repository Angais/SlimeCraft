using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// A full-screen page drawn on top of the HUD (inventory, crafting, creative palette, chat). The Hud module owns
    /// at most one open page, feeds it pointer and keyboard events read from Unity's legacy Input, and draws it after
    /// the HUD every frame. All coordinates are GUI pixels with the origin at the top-left.
    /// </summary>
    internal abstract class HudScreen
    {
        /// <summary>Id reported by <c>/screen</c> and <c>IHud</c>.</summary>
        public abstract string Name { get; }

        /// <summary>Current GUI size.</summary>
        public int ViewW, ViewH;

        protected HudModule Hud;
        protected McFont Font => Hud.Font;

        /// <summary>The page becomes the open page.</summary>
        public virtual void Open(HudModule hud, int viewW, int viewH)
        {
            Hud = hud;
            ViewW = viewW;
            ViewH = viewH;
        }

        /// <summary>Called every frame (before input and drawing) with the current GUI size.</summary>
        public virtual void Fit(int viewW, int viewH)
        {
            ViewW = viewW;
            ViewH = viewH;
        }

        /// <summary>Fixed 20 Hz update while the page is open.</summary>
        public virtual void Step() { }

        public abstract void Draw(Gui g, int mouseX, int mouseY, float partialTick);

        /// <summary>A mouse button went down (button 0 left, 1 right, 2 middle).</summary>
        public virtual bool PointerDown(double x, double y, int button, bool doubleClick) => false;

        /// <summary>A mouse button came back up.</summary>
        public virtual bool PointerUp(double x, double y, int button) => false;

        /// <summary>The mouse moved while <paramref name="button"/> is held.</summary>
        public virtual bool PointerMove(double x, double y, int button) => false;

        /// <summary>Mouse wheel; positive is away from the player.</summary>
        public virtual bool Wheel(double x, double y, double delta) => false;

        /// <summary>A key went down (navigation keys also repeat). Escape leaves the page unless overridden.</summary>
        public virtual bool Key(KeyCode key, bool ctrl, bool shift)
        {
            if (key != KeyCode.Escape) return false;
            Hud.CloseScreen();
            return true;
        }

        /// <summary>A character produced by the keyboard (letters, digits, backspace...).</summary>
        public virtual void Typed(char c) { }

        /// <summary>The page is being closed or replaced by another one.</summary>
        public virtual void Closed() { }

        /// <summary>True while a text field has the focus: letters are typed instead of acting as hotkeys.</summary>
        public virtual bool WantsText => false;

        /// <summary>Shades the world behind an item page: a vertical gradient from 0xC0101010 to 0xD0101010.</summary>
        protected void DimWorld(Gui g)
        {
            g.DrawGradient(0, 0, ViewW, ViewH, 0xC0101010, 0xD0101010);
        }
    }
}
