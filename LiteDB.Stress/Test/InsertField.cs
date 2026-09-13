using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;

namespace LiteDB.Stress
{
    public enum InsertFieldType { Name, Int, Guid, Bool, Now, Binary, Date, Json }

    public class InsertField
    {
        private readonly Random _rnd = new Random();

        #region Names

        private static readonly string[] _firstNames = new string[]
        {
            "Blaze", "Zenia", "Malik", "Palmer", "Valentine", "Quinn", "Preston", "John", "Ferdinand", "Cassidy", "Kai", "Alma", "Cullen",
            "Colorado", "Sean", "Kadeem", "Faith", "Dexter", "Olympia", "Alden", "Gloria", "Dahlia", "Melissa", "Fiona", "Oliver", "Daryl",
            "Jillian", "Adrienne", "Hayes", "Aphrodite", "Noel", "Martha", "Dennis", "Grace", "Veronica", "Caesar", "Keane", "Kamal", "Eagan",
            "Shana", "Knox", "Amaya", "Carson", "Zeph", "Mara", "Tanner", "Echo", "Simon", "Edan", "Ralph", "Heidi", "David",
            "Leigh", "Adam", "Connor", "Walter", "Caesar", "Leo", "Plato", "Molly", "James", "Bevis", "Aaron", "Igor", "Addison",
            "Octavius", "Breanna", "Kermit", "Melyssa", "Nathaniel", "Graham", "Benedict", "Kessie", "Alfreda", "Kyla", "Fritz", "Barclay", "Jarrod",
            "Alexa", "Grady", "Tatiana", "Andrew", "Lesley", "Nero", "Cooper", "Kellie", "Nasim", "Elizabeth", "Colette", "Serina", "Skyler",
            "Eugenia", "Dacey", "Karyn", "Roth", "Steel", "Gage", "Athena", "Ryan", "Jemima", "Duncan", "Jason", "Shelby", "Mallory",
            "Isabelle", "Danielle", "Nicole", "Imelda", "Colleen", "Paki", "Lucas", "Micah", "Halee", "Russell", "Lee", "Haley", "Ifeoma",
            "Imogene", "McKenzie", "Neil", "Alexa", "Oren", "Alea", "Francis", "Victoria", "Veda", "Gregory", "Georgia", "Alfreda", "Xanthus",
            "Dai", "Harlan", "Wing", "Janna", "Linus", "Ebony", "Angelica", "Emerald", "Vivian", "Kerry", "Oren", "Ashton", "Alana",
            "Cade", "Louis", "Melinda", "Angelica", "Jesse", "Anastasia", "Carson", "Pascale", "Price", "Mannix", "Maisie", "Ian", "Rae",
            "Graham", "Yasir", "Baxter", "Callie", "David", "Kirk", "Gabriel", "Micah", "Penelope", "Amal", "Yvonne", "Aaron", "Laith",
            "Cathleen", "Sebastian", "Madonna", "Orlando", "Carla", "Jacob", "Medge", "Jin", "Patrick", "Vielka", "Tanya", "Kiayada", "Nyssa",
            "Devin", "Aline", "Hayden", "Ariana", "Lucius", "Aiko", "Dieter", "Amity", "Rhiannon", "Samantha", "Patience", "Mechelle", "Christopher",
            "Grady", "Gillian", "Galvin", "Simon", "Nina", "Kalia", "Jane", "Kay", "Ishmael", "Garrison", "Chloe", "Sasha", "Cade",
            "Glenna", "Chava", "Jackson", "Althea", "Gary", "Galvin", "Martena", "Candice", "Quinn", "Norman", "Howard", "Alden", "Cain",
            "Whitney", "Judah", "Hammett", "Mallory", "Len", "Cally", "Zorita", "Rahim", "Carter", "Dean", "Amal", "Abbot", "Slade",
            "Cara", "Ignacia", "Zenaida", "Holly", "Nell", "Amethyst", "Hiroko", "Kibo", "Kasimir", "Grace", "Graiden", "Lana", "Bert",
            "Vincent", "Ashton", "Upton", "Tate", "Rajah", "Tyler", "Tarik", "Zachery", "Petra", "Callum", "Kaseem", "Kelly", "Hayley",
            "Wynne", "Belle", "Stewart", "Oprah", "Tyler", "Coby", "Ingrid", "Hiram", "Brennan", "Velma", "Emery", "Dorian", "Elmo",
            "Conan", "Caldwell", "Margaret", "Alan", "Chava", "Oprah", "Curran", "Jordan", "Jescie", "Vanna", "Leila", "Drew", "Acton",
            "Hannah", "Patrick", "Sierra", "Vaughan", "Maxwell", "Maxine", "Colleen", "Aphrodite", "Brady", "Caleb", "Price", "Brody", "Donovan",
            "Daphne", "Quinn", "Stone", "Wang", "Orlando", "Emery", "Uta", "Todd", "Kelsie", "Gary", "Dane", "Hayfa", "Madison",
            "Daquan", "Orson", "Alisa", "Unity", "Amaya", "Serina", "Montana", "Vielka", "Vincent", "Kaseem", "Janna", "Dillon", "Evelyn",
            "Sharon", "Zena", "Logan", "Venus", "Ariana", "Shafira", "Alika", "Delilah", "Harding", "Dieter", "Barclay", "Jasper", "Lara",
            "Genevieve", "Germaine", "August", "Joel", "Tatum", "Olympia", "Rebekah", "Cally", "Cullen", "Janna", "Ruth", "Quyn", "Kelsie",
            "Brett", "Aphrodite", "Rigel", "Lani", "Clare", "Timothy", "Angela", "Victor", "Emi", "Eric", "Lillian", "Wade", "Marah",
            "Rebekah", "Dustin", "Amal", "Acton", "Leilani", "Daniel", "Jermaine", "Reece", "Ishmael", "Bernard", "Ina", "Mariko", "Zachary",
            "Deanna", "Natalie", "Dorian", "Tyrone", "Jorden", "Galvin", "Wyatt", "Caleb", "Mercedes", "Lev", "Honorato", "Dane", "Kane",
            "Nasim", "Tarik", "Lev", "Jane", "Adrian", "Karina", "Garrison", "Brittany", "Sophia", "Walker", "Anne", "Brent", "Rebekah",
            "Hedwig", "Brittany", "Rebecca", "Baker", "Rhea", "Brock", "Unity", "Christine", "Rose", "Heather", "Quail", "Ezekiel", "Cairo",
            "Haviva", "Bernard", "Nolan", "Jordan", "Prescott", "Zahir", "Aphrodite", "Hiram", "Marvin", "Roanna", "Ann", "Nehru", "Winter",
            "Beau", "Zane", "Vera", "Reuben", "Josiah", "Ira", "Ann", "Stone", "Mannix", "Deanna", "Cruz", "Reece", "Gray",
            "Chancellor", "Oren", "Hakeem", "Echo", "Ivor", "Joseph", "Karleigh", "Rooney", "Aileen", "Ezra", "Eleanor", "Walker", "Kirestin",
            "Rafael", "Keefe", "Clinton", "Avye", "Josephine", "Kristen", "Nash", "Carly", "Ezekiel", "Abbot", "Keiko", "Jasper", "Flynn",
            "Gray", "Urielle", "Eleanor", "Joshua", "Neville", "Cleo", "Ava", "Joshua", "Elizabeth", "Noel", "Ezekiel", "Melvin", "Steel",
            "Helen", "Genevieve", "Valentine", "Graham", "Grady", "Brielle", "Amos", "Brian", "Amos", "Ramona", "Leonard", "Imogene", "Hyatt",
            "Gay", "Hermione", "Raymond", "Meghan", "Francesca", "Brent", "Zachery", "Marvin", "Devin", "Aristotle", "Ian", "Herrod", "Angela",
            "Cally", "Kirby", "Faith", "Brynn", "Honorato", "Octavius", "Latifah", "Camden", "Zeus", "Buffy", "Teegan", "Victor", "Basia",
            "Amelia", "Robin", "Vincent", "Xander", "Hamish", "Fallon", "Jenna", "Aphrodite", "Genevieve", "Thor", "Rhonda", "Jonah", "Shoshana",
            "Bianca", "John", "Nolan", "Anika", "Kai", "Claudia", "Ira", "Yoshi", "Rashad", "Gay", "Josiah", "Yoshio", "Yuri",
            "Russell", "Amir", "Mariko", "Mallory", "Risa", "Kai", "Merritt", "Leo", "Phelan", "Guy", "Cheryl", "Jolene", "Jerry",
            "Florence", "Kelly", "Deborah", "Pascale", "Lilah", "Fiona", "Forrest", "Blake", "Deborah", "Hu", "Kerry", "Hilel", "Kane",
            "Michelle", "Adria", "Fitzgerald", "Winter", "Ella", "Kirk", "Olga", "Tallulah", "Odette", "Ali", "Deacon", "Jarrod", "Mariko",
            "Hasad", "Abdul", "Stella", "Lilah", "Burke", "Bert", "Mona", "Lewis", "Cameron", "Ezra", "Sacha", "Fitzgerald", "Cedric",
            "Martin", "Deborah", "Shana", "Shafira", "Nissim", "Athena", "Leandra", "Stewart", "Todd", "Leandra", "Josephine", "Ariel", "Mannix",
            "Gregory", "Montana", "Steven", "Allegra", "Bree", "Bevis", "Barbara", "Cole", "Katelyn", "Rinah", "Carolyn", "Perry", "Brooke",
            "Addison", "Rigel", "Erica", "Regina", "Amy", "Cassady", "Alyssa", "Fritz", "Emi", "Kiona", "Cailin", "Adele", "Matthew",
            "Samson", "Nicole", "Thane", "Hasad", "Jeanette", "Michelle", "Trevor", "Shaeleigh", "Doris", "Galvin", "Uma", "Linda", "Noble",
            "Howard", "Alexandra", "Raymond", "Jessamine", "Cassandra", "Cheryl", "Norman", "McKenzie", "Daquan", "MacKenzie", "Isaiah", "Dante", "Keaton",
            "Lucian", "Ann", "Latifah", "Ryder", "Ferris", "Catherine", "Illana", "Ashely", "Iola", "Charlotte", "Orson", "Jeremy", "Theodore",
            "Maisie", "Ashely", "Upton", "Ashton", "Victor", "Riley", "Ray", "Odette", "Mariko", "Celeste", "Ali", "Patrick", "Nell",
            "Dahlia", "Martha", "Piper", "Rooney", "Colin", "Elliott", "Winifred", "Todd", "Charissa", "Deirdre", "Gage", "Amery", "Oscar",
            "Joan", "Maris", "Francis", "Madaline", "Susan", "Jamalia", "Ralph", "Lacota", "Alika", "Lucian", "Evangeline", "Alea", "Wyatt",
            "Vera", "Cameran", "Chandler", "Fiona", "Bethany", "Samuel", "Herman", "Mia", "Hiroko", "Bevis", "Karina", "Xander", "Alec",
            "Philip", "Nomlanga", "Owen", "Gail", "Basil", "Isaiah", "Tanisha", "Robin", "Reuben", "Brett", "Stacey", "Stephen", "Jaime",
            "Winifred", "Unity", "Marah", "Ethan", "Emery", "Harper", "Ursa", "Zia", "Avye", "Keely", "Athena", "Herman", "Conan",
            "Ivy", "Karly", "Pascale", "Echo", "Zoe", "Ray", "Tyler", "Lamar", "Scott", "Sade", "Fletcher", "Blaze", "Jescie",
            "Judah", "Yvonne", "Mason", "Garrison", "Melodie", "Solomon", "Tashya", "Thaddeus", "Axel", "Raymond", "Maya", "Iola", "Jordan",
            "Sage", "Lev", "Jamalia", "Darius", "Justin", "Kamal", "Shelby", "Demetrius", "Jackson", "Nadine", "Porter", "Herrod", "Quynn",
            "Burton", "Ella", "Channing", "Serina", "Lucian", "Nadine", "Desirae", "Moses", "Vance", "Zachary", "Gavin", "Phyllis", "Malcolm",
            "Joshua", "Mia", "Kirby", "Christopher", "Walker", "Prescott", "Barbara", "Katelyn", "Byron", "Jarrod", "Dalton", "Damon", "Ella",
            "Erich", "Yen", "Octavia", "Hamilton", "Adele", "Gail", "Slade", "Piper", "Summer", "Cain", "Fiona", "Gabriel", "Stuart",
            "Dane", "Tatyana", "Stone", "Astra", "Byron", "Damian", "Aaron", "Kennan", "Brendan", "Germaine", "Kane", "Adam", "Elliott",
            "Clark", "Simon", "Lamar", "Oprah", "Randall", "Leandra", "Hayden", "Haley", "Cameran", "Christian", "Diana", "Fritz", "Signe",
            "Camilla", "Jena", "Grace", "Garrett", "Jescie", "Camilla", "David", "Orlando", "Veda", "MacKensie", "Darius", "Quamar", "Arthur",
            "Leandra", "Ryder", "Rebekah", "Malcolm", "Hedy", "Aphrodite", "Palmer", "Knox", "Byron", "Armand", "Brooke", "Carlos", "Tara",
            "Bell", "Karina", "Vera", "Irene", "Calista", "Liberty", "Imelda", "Plato", "Fleur", "Ifeoma", "Olga", "Rogan", "Jescie",
            "Amaya", "Cheryl", "Stewart", "Hedwig", "Ignacia", "Solomon", "Abel", "Vivian", "Constance", "Renee", "Judah", "Leandra", "Donna",
            "Chadwick", "Fatima", "Travis", "Malik", "Edan", "Devin", "Tasha", "Walter", "Kitra", "Mona", "Maya", "Haley", "Talon",
            "Hollee", "Cedric", "Ann", "Raymond", "Rachel", "Celeste", "Raja", "Teegan", "Sylvia", "Scott", "Gloria", "Tiger", "Nigel",
            "Denise", "Velma", "Akeem", "Neville", "Aristotle", "Darius", "Hamilton", "Armand", "Nathaniel", "Asher", "Shelly", "Bianca", "Gary",
            "Elijah", "Mia", "Quinn", "Quentin", "Dane", "Shoshana", "Lenore", "Gwendolyn", "Brenden", "Deacon", "Davis", "Ulric", "Bruno",
            "Hanna", "Tashya", "Kennan", "Nomlanga", "Quyn", "Uriel", "Donna", "Ariel", "Carl", "Gray", "Louis", "Daphne", "Amir",
            "Illiana", "Ryan", "Brenden", "Cathleen", "Arden", "Olga", "Axel", "Tanek", "Bell", "Beau", "Mira", "Audra", "Hall",
            "Perry", "Jason", "Levi", "Deacon", "Reese", "Arsenio", "Molly", "Cade", "Mufutau", "Cole", "Tamara", "Ann"
        };

        private static readonly string[] _lastNames = new string[]
        {
            "Hakeem", "Jena", "Donna", "Cameron", "Ramona", "Guy", "Frances", "Price", "Sydnee", "Miriam", "Quinlan", "Margaret", "Mikayla", "Chester",
            "Dean", "Priscilla", "Astra", "Debra", "Kasimir", "Kane", "Amelia", "Phoebe", "Justina", "Brady", "Sybil", "Zeus", "Amethyst", "Dante",
            "Katell", "Keegan", "Olympia", "Rhona", "Quinn", "Lamar", "Aidan", "Alea", "John", "Nathan", "Kirk", "Brenden", "Kato", "Olympia",
            "Zeus", "Cadman", "Dalton", "Maisie", "Curran", "Jelani", "Xyla", "Andrew", "Nissim", "Florence", "Kasimir", "Daphne", "Joseph", "Seth",
            "Nerea", "Peter", "Emi", "Beau", "Ima", "Victoria", "Adena", "Vernon", "Yasir", "Piper", "Sylvia", "Hedy", "Aidan", "Bruce",
            "May", "Zelenia", "Logan", "Hillary", "Pamela", "Christine", "Ira", "Cain", "Lucius", "Troy", "Ori", "Simon", "Tatum", "Orson",
            "Seth", "Perry", "Eagan", "Mollie", "Olympia", "Rhiannon", "Imelda", "Shaeleigh", "Octavius", "Ulysses", "Sade", "Maia", "Lacy", "Anthony",
            "Beatrice", "Igor", "Rhiannon", "Kirby", "Walker", "Dominic", "Hamish", "Georgia", "Rose", "Emma", "Francis", "Kerry", "Marsden", "Haviva",
            "Palmer", "Hermione", "Omar", "Lana", "Susan", "Yuli", "Ray", "Basia", "Blake", "Keefe", "Dennis", "Sasha", "Sara", "Dylan",
            "Elliott", "Alden", "Daryl", "Samson", "Emily", "Gage", "Pandora", "Fallon", "Yolanda", "Courtney", "Ori", "Eve", "Patience", "Jameson",
            "Lionel", "Zephr", "Zahir", "Ray", "Kennedy", "Stacey", "Zephania", "Yuri", "Cassady", "Ingrid", "Colt", "Cain", "Frances", "Sean",
            "Travis", "Stuart", "Kimberly", "Risa", "Guy", "Fleur", "Hedda", "Duncan", "Skyler", "Leah", "Rhiannon", "Jack", "Dale", "Wesley",
            "Halee", "Lance", "Lionel", "Gwendolyn", "Marshall", "Elizabeth", "Kessie", "Marsden", "McKenzie", "Cheyenne", "Shafira", "Bernard", "Lana", "Sydnee",
            "MacKenzie", "Cade", "Georgia", "Emma", "Hiram", "Mara", "Indira", "Len", "Desiree", "Glenna", "Hadley", "Caldwell", "Emerson", "Teegan",
            "Jack", "Kane", "Demetrius", "Hilel", "Hilel", "Joan", "Malik", "Derek", "Flynn", "Berk", "Ishmael", "Stuart", "Brett", "Lani",
            "Uriah", "Samantha", "Yeo", "Chava", "Joel", "Candace", "Jasmine", "Stuart", "Reuben", "Asher", "Thomas", "Mufutau", "Xavier", "Sybill",
            "Heather", "Jameson", "Slade", "Xenos", "Aurelia", "Darius", "Oscar", "Cullen", "Fay", "Violet", "Lenore", "Alfreda", "Sara", "TaShya",
            "Forrest", "Kellie", "Hu", "Robin", "Lawrence", "Isadora", "Kimberly", "Marcia", "Sandra", "Fuller", "Rana", "Reuben", "Louis", "Hector",
            "Davis", "Germane", "Arthur", "Erica", "Marah", "Maite", "Keiko", "Jade", "Zelda", "Xyla", "Cheryl", "Price", "Yardley", "Alexis",
            "Portia", "MacKensie", "Rae", "Beverly", "Sopoline", "Kiayada", "Shea", "Rafael", "Nissim", "Geoffrey", "Nash", "Germane", "Walter", "Uta",
            "Fiona", "Illana", "Timothy", "Lacy", "Rajah", "Lance", "Yvonne", "Holmes", "Connor", "Nathaniel", "Inez", "Prescott", "Avye", "Igor",
            "Yvonne", "Ishmael", "Urielle", "Sigourney", "Gisela", "Idona", "John", "Channing", "Quyn", "Sylvester", "Shana", "Damian", "Darius", "Libby",
            "Sybill", "Cameron", "Jermaine", "Alan", "Aileen", "Stella", "Griffin", "Indigo", "Stephen", "Drake", "Ariana", "Berk", "Jelani", "Lucius",
            "Erich", "Mona", "Howard", "Abbot", "Uma", "Wade", "Rashad", "Ginger", "Victoria", "John", "Leigh", "Hilel", "Kimberly", "Porter",
            "Ann", "Zelenia", "Ruby", "Allistair", "Octavia", "Nissim", "Dacey", "Sylvia", "Judah", "Matthew", "Gregory", "Elton", "Xena", "MacKensie",
            "Aaron", "Farrah", "Kiara", "Dexter", "Ulla", "Zia", "Jenette", "Inez", "Finn", "Neil", "Iola", "Courtney", "Preston", "Mechelle",
            "August", "Wilma", "Chaim", "Willow", "Allen", "Kibo", "Jena", "Ayanna", "Hayden", "Jerome", "Colorado", "Shana", "Kato", "Thomas",
            "Christopher", "Lucas", "Moana", "Dane", "Justina", "Nell", "Signe", "Maryam", "Halla", "Mohammad", "Louis", "Amethyst", "Berk", "Julie",
            "Pascale", "Ezekiel", "Lana", "Fleur", "Elaine", "Tanek", "Ashton", "Ryan", "Leigh", "Blythe", "Mannix", "Quinn", "Hector", "Lael",
            "Ignatius", "Julie", "Wade", "Bruno", "Declan", "Jana", "Clementine", "Lara", "Heather", "Jerome", "Graham", "Kamal", "Rhoda", "Kelly",
            "Oprah", "Odysseus", "Montana", "Petra", "Nehru", "Amelia", "Louis", "Finn", "Erich", "Irma", "Hadley", "Keelie", "Melissa", "Neve",
            "Dexter", "Erasmus", "Theodore", "Camden", "Wesley", "Kiara", "Patience", "Ocean", "Petra", "Brody", "Caleb", "Emerald", "Hedwig", "Amy",
            "Hop", "Nigel", "Indigo", "Hashim", "Hedda", "Andrew", "Devin", "Rashad", "Demetria", "Fritz", "Katell", "Jelani", "Ingrid", "Claire",
            "Cailin", "Priscilla", "Flynn", "Rhonda", "Yolanda", "Belle", "Harlan", "Neville", "Yael", "Jocelyn", "Fritz", "Yuri", "Caldwell", "Nissim",
            "Timothy", "Darius", "Michael", "Patrick", "Claire", "Hadassah", "Eugenia", "Jeanette", "Hoyt", "Aretha", "Reed", "Constance", "Wyatt", "Zia",
            "Sigourney", "Jenna", "Daniel", "Yolanda", "Edan", "Winifred", "Sebastian", "Peter", "Kiona", "Stuart", "Ivor", "Zelda", "Whoopi", "Rae",
            "Naida", "Adele", "Kaseem", "Jesse", "Chiquita", "Mufutau", "Randall", "Beau", "Devin", "Laith", "Tashya", "Althea", "Kevyn", "Dominique",
            "Erasmus", "Nayda", "Medge", "Driscoll", "Kennan", "Raya", "Cooper", "Kadeem", "Dominic", "Ebony", "Octavius", "Eric", "Ruby", "Allen",
            "Unity", "Clark", "Kaseem", "Brooke", "Preston", "Vivian", "Lila", "Ila", "Blaze", "Miranda", "Hall", "Wyoming", "Eagan", "Caesar",
            "Ruby", "Price", "Jasmine", "Mannix", "Stone", "Yoshio", "Hayfa", "Dieter", "Karen", "Melanie", "Rhona", "Jin", "Graiden", "Logan",
            "Sophia", "Zahir", "Kaitlin", "Cullen", "Ivory", "Cooper", "Addison", "Dustin", "Ann", "Grant", "Samuel", "Ezra", "Shaine", "Lois",
            "Ciaran", "Myra", "Signe", "Aquila", "Nell", "Tanisha", "Reese", "Sylvester", "Delilah", "Guy", "Cyrus", "Ebony", "Norman", "Cain",
            "Silas", "Urielle", "Neve", "Dillon", "Francesca", "Brynn", "Laura", "Montana", "Ila", "Jessamine", "Nell", "April", "Ulysses", "Zoe",
            "Nathaniel", "Caryn", "Jada", "Blaze", "Maya", "Thomas", "Chaim", "Carl", "Doris", "Orson", "Joan", "Kuame", "Noelani", "Dana",
            "Aileen", "Aurelia", "Brynne", "Oleg", "Chancellor", "Genevieve", "Sonya", "Jakeem", "Velma", "Ariel", "Seth", "Althea", "Hollee", "Addison",
            "Lael", "Gary", "Gavin", "Halee", "Cruz", "Laurel", "Talon", "Avye", "David", "Nevada", "Driscoll", "Martin", "Gloria", "Amos",
            "Minerva", "Sawyer", "George", "Linus", "Quemby", "Doris", "Vance", "Ariana", "Derek", "Keane", "Lavinia", "Henry", "Rigel", "Sade",
            "Elaine", "Gloria", "Brady", "Mark", "Timothy", "Sylvia", "Leroy", "Shellie", "Lucian", "Stewart", "Barbara", "Jared", "Zeph", "Kaitlin",
            "Andrew", "Dara", "Carol", "Cynthia", "Roth", "Tanner", "Edward", "Anjolie", "Iola", "Castor", "Hamish", "Heather", "Beau", "Hyatt",
            "Lavinia", "Oren", "Charde", "Jack", "Jaquelyn", "Audrey", "Lev", "Ella", "Tanner", "Paki", "Melodie", "Marshall", "Tanek", "Chandler",
            "Malcolm", "Kessie", "Eleanor", "India", "Cade", "Nehru", "Ryan", "Lana", "Ella", "Jada", "Florence", "Brent", "Nolan", "Lacy",
            "Merritt", "Hall", "Dana", "Bruce", "Upton", "Jakeem", "Cameron", "Ginger", "Iola", "Christian", "Oscar", "Troy", "Delilah", "Katell",
            "Maxwell", "Rhiannon", "Adrienne", "Angelica", "Uriel", "Kiayada", "Neil", "Remedios", "Jordan", "Tyrone", "Caesar", "Craig", "Dante", "Hector",
            "Sylvia", "Samuel", "Aurora", "Wade", "Julian", "Steven", "Bruce", "Barry", "Carly", "Hiram", "Desirae", "Tyrone", "Lionel", "Hammett",
            "Driscoll", "Jenna", "Hayden", "Althea", "Raymond", "Genevieve", "Tanya", "Quentin", "Blossom", "Joshua", "Justin", "Adrian", "Ronan", "Nevada",
            "Dylan", "Ursa", "Beck", "Barry", "Reese", "Xaviera", "Brittany", "Daniel", "Rajah", "Jada", "Stacy", "Indigo", "Colt", "Daryl",
            "Cadman", "Wynter", "Lee", "Wang", "Elizabeth", "Hall", "Jonah", "Armando", "Wilma", "Caleb", "Coby", "Gage", "Mary", "Grady",
            "Travis", "Daquan", "Cain", "Cara", "Hammett", "Zenia", "Claire", "Graiden", "Nolan", "Price", "Sacha", "Quinlan", "Doris", "Doris",
            "Ocean", "Alexis", "Valentine", "Chloe", "Omar", "Raja", "Porter", "Allistair", "Madeline", "Ruth", "Amy", "MacKensie", "Rhonda", "Nomlanga",
            "Zachery", "Stuart", "Evangeline", "Brianna", "Kyla", "Odessa", "Joel", "Fatima", "Jasmine", "Keane", "Lewis", "Cora", "Amos", "Kyra",
            "Kasimir", "Zachery", "Bethany", "Macey", "Gregory", "Cedric", "Christopher", "Branden", "Elijah", "Suki", "Nelle", "Alice", "Cathleen", "Darrel",
            "Eugenia", "Hayfa", "Benedict", "Cally", "TaShya", "Chadwick", "Walker", "Tucker", "Cruz", "Abbot", "Harriet", "Ivan", "Tanek", "Quinn",
            "Denise", "Sybill", "Rae", "Logan", "Lane", "Kaseem", "Hammett", "Jena", "Althea", "Jayme", "Andrew", "Aspen", "Noelle", "Audrey",
            "Yael", "Nathaniel", "Inez", "Carl", "Sacha", "Ayanna", "Mona", "Britanney", "Amery", "Ashton", "Suki", "Kyle", "Zachary", "Ursa",
            "Katell", "Cathleen", "Zeus", "Aileen", "Carol", "Martina", "Blythe", "Yardley", "Ira", "Virginia", "Gisela", "Erich", "MacKensie", "Ira",
            "Amy", "Damian", "Ian", "Herrod", "Wendy", "Michael", "Basil", "Sandra", "Hanae", "Laura", "Raya", "Katelyn", "Yoko", "Hayley",
            "Lester", "Damon", "Brian", "Kennan", "Zenia", "Karleigh", "Zorita", "Tamekah", "Christian", "Halla", "Dante", "Bernard", "Colin", "Aphrodite",
            "Cheyenne", "Stone", "Jada", "Beck", "Adrian", "Sylvester", "Ila", "Rhiannon", "Ryan", "Sierra", "Kelly", "Gavin", "Jesse", "Vincent",
            "Paula", "Velma", "Illiana", "Emmanuel", "Fletcher", "Kyla", "Lawrence", "Gage", "Micah", "Jordan", "Clayton", "Aphrodite", "Gil", "Amir",
            "Levi", "Chadwick", "Porter", "Ivor", "Rhonda", "Kay", "Chandler", "Diana", "Ainsley", "Jolie", "Indira", "Victor", "Cassady", "Wynter",
            "Sean", "Candice", "Zelenia", "Demetrius", "Nissim", "Martha", "Christian", "Dahlia", "Tyler", "Barry", "Griffith", "Quyn", "Cairo", "Xaviera",
            "Joseph", "Desirae", "Kasimir", "Rylee", "Larissa", "Sandra"
        };

        #endregion

        public string Name { get; }
        public InsertFieldType Type { get; }
        public int StartIntRange { get; }
        public int EndIntRange { get; }
        public DateTime StartDateRange { get; }
        public int DaysDateRange { get; }
        public BsonValue Value { get; }

        public InsertField(XmlElement el)
        {
            this.Name = el.Name;
            this.Type = el.GetAttribute("type").ToLower() switch
            {
                "name" => InsertFieldType.Name,
                "int" => InsertFieldType.Int,
                "guid" => InsertFieldType.Guid,
                "bool" => InsertFieldType.Bool,
                "now" => InsertFieldType.Now,
                "binary" => InsertFieldType.Binary,
                "date" => InsertFieldType.Date,
                _ => InsertFieldType.Json
            };

            var range = el.GetAttribute("range");

            if (this.Type == InsertFieldType.Int || this.Type == InsertFieldType.Binary)
            {
                this.StartIntRange = int.Parse(range.Split('~').First());
                this.EndIntRange = int.Parse(range.Split('~').Last());
            }
            else if (this.Type == InsertFieldType.Date)
            {
                this.StartDateRange = DateTime.Parse(range.Split('~').First());
                var endDateRange = DateTime.Parse(range.Split('~').Last());
                this.DaysDateRange = (int)endDateRange.Subtract(this.StartDateRange).TotalDays;
            }

            this.Value = this.Type == InsertFieldType.Json ?
                JsonSerializer.Deserialize(el.InnerText) :
                null;
        }

        public BsonValue GetValue()
        {
            switch(this.Type)
            {
                case InsertFieldType.Name: return _firstNames[_rnd.Next(0, _firstNames.Length - 1)] + " " + _lastNames[_rnd.Next(0, _lastNames.Length - 1)];
                case InsertFieldType.Int: return _rnd.Next(this.StartIntRange, this.EndIntRange);
                case InsertFieldType.Guid: return Guid.NewGuid();
                case InsertFieldType.Bool: return _rnd.NextDouble() > .5;
                case InsertFieldType.Now: return DateTime.Now;
                case InsertFieldType.Binary: return new byte[_rnd.Next(this.StartIntRange, this.EndIntRange)];
                case InsertFieldType.Date: return this.StartDateRange.AddDays(_rnd.Next(this.DaysDateRange));
                default: return this.Value;
            }
        }
    }
}
