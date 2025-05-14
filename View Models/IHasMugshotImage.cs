namespace NPC_Plugin_Chooser_2.View_Models;

public interface IHasMugshotImage
{
    // Displayed size, affected by ImagePacker and zoom
    public double ImageWidth { get; set; }
    public double ImageHeight { get; set; }

    // Original dimensions of the bitmap in pixels
    public int OriginalPixelWidth { get; set; }
    public int OriginalPixelHeight { get; set; }

    // Original dimensions of the image in Device Independent Pixels (DIPs)
    // These are calculated considering the image's DPI.
    public double OriginalDipWidth { get; set; }
    public double OriginalDipHeight { get; set; }
    public double OriginalDipDiagonal { get; set; } // Calculated from OriginalDipWidth and OriginalDipHeight

    public bool HasMugshot { get; }
    public bool IsVisible { get; } // This should be controlled by VM_NpcSelectionBar/VM_Mods
    public string ImagePath { get; set; }
}