using ClassLibrarySecond;
using AttributeGenerator;
using ClassLibrarySecond.Interfaces;

namespace ClassLibrary1;

[AutoImplementProperties(typeof(IUserInterface), typeof(IUserInterface2))]
public partial class UserClass
{
    public string UserProp { get; set; }
}