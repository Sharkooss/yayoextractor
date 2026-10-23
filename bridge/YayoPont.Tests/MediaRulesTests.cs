using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    public class MediaRulesTests
    {
        [Theory]
        [InlineData("IMG_1.JPG", "photo")]
        [InlineData("photo.heic", "photo")]
        [InlineData("scan.png", "photo")]
        [InlineData("VID_2.mp4", "video")]
        [InlineData("clip.MOV", "video")]
        [InlineData("notes.txt", null)]
        [InlineData("sans-extension", null)]
        public void Kind_depend_de_l_extension(string name, string expected)
        {
            Assert.Equal(expected, MediaRules.Kind(name));
        }

        [Theory]
        [InlineData("DCIM/Camera/20261021_101010.jpg", "camera")]
        [InlineData("DCIM/Screenshots/Screenshot_1.png", "screenshots")]
        [InlineData("Pictures/Screenshots/Screenshot_2.png", "screenshots")]
        [InlineData("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-1.jpg", "whatsapp")]
        [InlineData("WhatsApp/Media/WhatsApp Video/VID-1.mp4", "whatsapp")]
        [InlineData("Pictures/Messenger/x.jpg", "other")]
        [InlineData("Download/y.jpg", "other")]
        public void Source_depend_du_chemin(string path, string expected)
        {
            Assert.Equal(expected, MediaRules.Source(path));
        }

        [Theory]
        [InlineData(".thumbnails", "DCIM", true)]
        [InlineData(".trashed", "", true)]
        [InlineData("data", "Android", true)]
        [InlineData("obb", "Android", true)]
        [InlineData("media", "Android", false)]
        [InlineData("WhatsApp Stickers", "Android/media/com.whatsapp/WhatsApp/Media", true)]
        [InlineData("cache", "Pictures", true)]
        [InlineData("Camera", "DCIM", false)]
        [InlineData("data", "Pictures", false)]
        public void SkipFolder_ecarte_les_dossiers_inutiles(string name, string parent, bool expected)
        {
            Assert.Equal(expected, MediaRules.SkipFolder(name, parent));
        }
    }
}
