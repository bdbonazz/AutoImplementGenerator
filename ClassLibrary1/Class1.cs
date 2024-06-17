using ClassLibrarySecond;
using ClassLibrarySecond.Interfaces;
using AttributeGenerator;

namespace ClassLibrary1;

[AutoImplementProperties(nameof(IUserInterface), nameof(IUserInterface2))]
public partial class UserClass
{
    public string UserProp { get; set; }
}