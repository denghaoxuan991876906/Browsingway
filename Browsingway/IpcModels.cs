namespace Browsingway;

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
