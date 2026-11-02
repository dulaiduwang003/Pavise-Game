namespace PaviseApp
{
	internal struct DsState
	{
		public bool Enabled;

		public bool Busy;

		public bool Focused;

		public float Hover;

		public float Press;

		public bool Interactive
		{
			get
			{
				if (Enabled)
				{
					return !Busy;
				}
				return false;
			}
		}

		public float HoverIfEnabled
		{
			get
			{
				if (!Enabled)
				{
					return 0f;
				}
				return Hover;
			}
		}
	}
}
