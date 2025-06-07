using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace AreWeThereYet;

public class AreWeThereYet : BaseSettingsPlugin<AreWeThereYetSettings>
{
    public override bool Initialise()
    {
        //Perform one-time initialization here

        //Maybe load you custom config (only do so if builtin settings are inadequate for the job)
        //var configPath = Path.Join(ConfigDirectory, "custom_config.txt");
        //if (File.Exists(configPath))
        //{
        //    var data = File.ReadAllText(configPath);
        //}

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        //Perform once-per-zone processing here
        //For example, Radar builds the zone map texture here
    }

    public override Job Tick()
    {
        //Perform non-render-related work here, e.g. position calculation.
        //This method is still called on every frame, so to really gain
        //an advantage over just throwing everything in the Render method
        //you have to return a custom job, but this is a bit of an advanced technique
        //here's how, just in case:
        //return new Job($"{nameof(AreWeThereYet)}MainJob", () =>
        //{
        //    var a = Math.Sqrt(7);
        //});

        //otherwise, just run your code here
        //var a = Math.Sqrt(7);
        return null;
    }

    public override void Render()
    {
        //Any Imgui or Graphics calls go here. This is called after Tick
        Graphics.DrawText($"Plugin {GetType().Name} is working.", new Vector2(100, 100), Color.Red);
    }

    public override void EntityAdded(Entity entity)
    {
        //If you have a reason to process every entity only once,
        //this is a good place to do so.
        //You may want to use a queue and run the actual
        //processing (if any) inside the Tick method.
    }
}