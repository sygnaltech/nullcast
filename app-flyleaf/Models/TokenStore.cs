using System;

namespace VideoPlayer.Models
{
    public class TokenStore
    {
        public string   AccessToken  { get; set; } = "";
        public string   RefreshToken { get; set; } = "";
        public DateTime ExpiresAt    { get; set; }
        public string   DisplayName  { get; set; } = "";
        public string   Email        { get; set; } = "";
    }
}
