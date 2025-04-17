namespace NPC_Plugin_Chooser_2.View_Models;

public interface IHasMugshotImage
{
    public double ImageWidth { get; set; }
    public double ImageHeight { get; set; }
    public bool HasMugshot { get; }
    public bool IsVisible { get; }
    public string ImagePath { get; set; }
}