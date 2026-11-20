namespace PaviseApp
{
    internal sealed class LolLcuCredentials
    {
        public readonly int Port;

        public readonly string Token;

        public LolLcuCredentials(int port, string token)
        {
            Port = port;
            Token = token;
        }
    }
}
