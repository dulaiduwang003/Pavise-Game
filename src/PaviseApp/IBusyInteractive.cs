namespace PaviseApp
{
	internal interface IBusyInteractive
	{
		bool Busy { get; set; }

		bool CanInteract { get; }
	}
}
