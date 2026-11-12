# Didar CRM — تخصیص خودکار درخواست

[English version](README.en.md)

سرویس ASP.NET Core درخواست‌ها را ثبت می‌کند و Worker پس‌زمینه آن‌ها را بر اساس تخصص، ظرفیت و اولویت به کارشناسان تخصیص می‌دهد. دو نمونهٔ مستقل برنامه می‌توانند روی یک PostgreSQL مشترک کار کنند.

## اجرای آزمون‌های پذیرش

به **.NET SDK 10** و **Docker** نیاز دارید. فرمان‌ها را از ریشهٔ پروژه در PowerShell اجرا کنید. اگر PostgreSQL 16 در دسترس دارید، می‌توانید از اتصال همان نمونه استفاده کنید. نقش تست باید بتواند پایگاه موقت بسازد و حذف کند.

~~~powershell
docker run --detach --name didar-review-db --publish 127.0.0.1:54330:5432 --env POSTGRES_USER=didar --env POSTGRES_PASSWORD=didar-local --env POSTGRES_DB=postgres postgres:16
docker exec didar-review-db pg_isready -U didar -d postgres
~~~

وقتی خروجی فرمان دوم `accepting connections` شد، آزمون‌ها را اجرا کنید. برای استفادهٔ دوباره از کانتینر متوقف‌شده، `docker start didar-review-db` را اجرا کنید.

~~~powershell
$env:DidarTests__Postgres = 'Host=127.0.0.1;Port=54330;Database=postgres;Username=didar;Password=didar-local'
dotnet test tests/Didar.IntegrationTests/Didar.IntegrationTests.csproj
~~~

Fixture روی PostgreSQL واقعی پایگاه موقت ایجاد می‌کند، migrationها را اعمال می‌کند، دادهٔ هر سناریو را می‌سازد و در پایان پایگاه موقت را حذف می‌کند. آزمون‌های چندنمونه‌ای دو فرایند مستقل برنامه را خودشان اجرا می‌کنند. آماده‌سازی دستی داده برای اجرای تست لازم نیست.

### سناریوهای اصلی

| سناریو | آزمون و نتیجهٔ مورد انتظار |
| --- | --- |
| انتخاب کارشناس و نبود گزینه | [AssignmentTests](tests/Didar.IntegrationTests/AssignmentTests.cs) — کمترین نسبت بار به ظرفیت در میان کارشناسان واجد شرایط برنده می‌شود. بدون گزینه، درخواست `Pending` می‌ماند. |
| ۲۰ درخواست و ظرفیت ۵ | [MultiInstanceAssignmentTests](tests/Didar.IntegrationTests/MultiInstanceAssignmentTests.cs) — Fixture بیست درخواست متمایز را در صف می‌گذارد و دو Worker روی پایگاه مشترک آن‌ها را پردازش می‌کنند. نتیجه دقیقاً ۵ `Assigned` و ۱۵ `Pending` است. |
| ثبت تکراری از دو نمونه | [ConcurrentRegistrationTests](tests/Didar.IntegrationTests/ConcurrentRegistrationTests.cs) — بیست فراخوانی HTTP با یک `RequestKey` مشترک، یک رکورد و یک `RequestId` ایجاد می‌کنند. |
| رقابت روی یک درخواست یا ظرفیت مشترک | [DeliveryAcceptanceTests](tests/Didar.IntegrationTests/DeliveryAcceptanceTests.cs) و [MultiInstanceAssignmentTests](tests/Didar.IntegrationTests/MultiInstanceAssignmentTests.cs) — یک درخواست فقط یک بار تخصیص می‌گیرد و پس از رقابت، رتبهٔ کارشناسان با دادهٔ تازه محاسبه می‌شود. |
| آزادشدن ظرفیت، اولویت و Aging | [RequestLifecycleTests](tests/Didar.IntegrationTests/RequestLifecycleTests.cs) و [QueuePolicyTests](tests/Didar.IntegrationTests/QueuePolicyTests.cs) — بستن درخواست ظرفیت را آزاد می‌کند، درخواست فوری مقدم است، درخواست عادی با گذشت زمان ارتقا می‌یابد و صف بدون کارشناس مانع پردازش تخصص دیگر نمی‌شود. |

برای اجرای فقط تست ظرفیت ۵، بعد از تنظیم اتصال بالا اجرا کنید.

~~~powershell
dotnet test tests/Didar.IntegrationTests/Didar.IntegrationTests.csproj --filter 'FullyQualifiedName~TwentyRequestsForCapacityFiveAssignExactlyFive'
~~~

## اجرای اختیاری API و Swagger

با همان کانتینر، یک پایگاه توسعهٔ جدا برای اجرای دستی ایجاد کنید. `dotnet ef database update` پایگاه و جداول را آماده می‌کند.

~~~powershell
$env:ConnectionStrings__Didar = 'Host=127.0.0.1;Port=54330;Database=didar_dev;Username=didar;Password=didar-local'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet tool install --global dotnet-ef --version 10.0.14
dotnet ef database update --project src/Didar.Api --startup-project src/Didar.Api
dotnet run --project src/Didar.Api --no-launch-profile -- --urls http://127.0.0.1:5080
~~~

[Swagger UI](http://127.0.0.1:5080/swagger/index.html) مسیرهای ثبت، خواندن، بستن و لغو درخواست و تغییر ظرفیت یا وضعیت کارشناس را نشان می‌دهد. برای ثبت دستی، `specialtyId` باید به یک تخصص موجود اشاره کند. برای مشاهدهٔ تخصیص خودکار، همان تخصص باید دست‌کم یک کارشناس فعال با ارتباط فعال و ظرفیت آزاد داشته باشد. داده‌های موقت آزمون در پایگاه توسعه در دسترس نیستند.

## تصمیم‌های طراحی

- **انتخاب کارشناس** — بار شامل همهٔ درخواست‌های `Assigned` کارشناس در تمام تخصص‌هاست. در نسبت مساوی، کارشناس بدون سابقهٔ تخصیص یا با قدیمی‌ترین `LastAssignedAt` مقدم است تا فرصت میان کارشناسان هم‌بار توزیع شود. `ExpertId` تساوی نهایی را به‌صورت پایدار حل می‌کند.
- **ثبت و تخصیص هم‌زمان** — PostgreSQL یکتایی `RequestKey` را تضمین می‌کند. تخصیص در تراکنش `Serializable` با ترتیب قفل تخصص، کارشناس و درخواست انجام می‌شود. تغییر وضعیت و ثبت زمان تخصیص با هم commit می‌شوند. هنگام خطای serialization، تلاش با تراکنش تازه از انتخاب سر صف و رتبه‌بندی شروع می‌شود.
- **سیاست صف** — در هر تخصص، درخواست فوری مقدم است. درخواست عادی پس از ۵ دقیقه به سطح اولویت مؤثر فوری می‌رسد. در یک سطح، زمان ثبت قدیمی‌تر و سپس شناسهٔ کوچک‌تر مقدم‌اند. برای مثال یک درخواست عادی با ۶ دقیقه انتظار، از درخواست فوری با ۱ دقیقه انتظار در همان تخصص جلوتر است. Aging داخل هر تخصص اعمال می‌شود و برای رقابت میان تخصص‌ها بر سر یک کارشناس مشترک، تقدم سراسری یا سقف انتظار تعریف نشده است.
- **بازیابی** — ردیف‌های `Pending` در PostgreSQL منبع کار Worker هستند. بررسی در شروع و بررسی دوره‌ای، کار ثبت‌شده را پس از توقف فرایند دوباره پیدا می‌کنند.

