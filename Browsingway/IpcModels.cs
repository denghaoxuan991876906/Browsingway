namespace Browsingway;

#pragma warning disable CS0649 // Fields assigned by Dalamud IPC deserialization

internal struct CreateOrUpdateArgs
{
	public string Name;
	public string Url;
	public int Width;
	public int Height;
	public float Zoom;
	public bool Locked;
}

internal struct SetVisibilityArgs
{
	public string Name;
	public bool Visible;
}

internal struct SetPositionArgs
{
	public string Name;
	public int? X;
	public int? Y;
}

internal struct SetDisabledArgs
{
	public string Name;
	public bool Disabled;
}
