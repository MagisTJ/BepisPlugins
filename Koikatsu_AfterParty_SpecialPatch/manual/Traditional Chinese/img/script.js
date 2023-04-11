$(function () {
	/*-------------------------------------------------------------------------------------*/

	
	//ページ内スクロール
	
	$('a').on('click', function(){
		var link= $(this).attr("href");
		var scl = $(link).offset().top;
		$("html,body").animate({scrollTop : scl - 130}, 400);

	});



	$('#menu > li').hover(function () {
		$('.subMenu', this).stop().animate({height: 'show'}, 300);
	}, function () {
		$('.subMenu', this).stop().animate({height: 'hide'}, 300);
	});





	$('#gtop').click(function () {
		$('body,html').animate({scrollTop: 0}, 300);
		return false;
	});


	//chara
	for (var i = 0 ; i < 5 ; i++){
		$('#cButton').append("<img src='img/chara/b"+i+".jpg'>");
		$('#cImage').append("<img src='img/chara/c"+i+".jpg'>");
	}

id=0;
	$(document)
	.on('mouseenter','#cButton img', function () {
		$('#cButton img').fadeTo(100,0.6);
		$(this).stop().fadeTo(100,1);
		$("#cImage").css({backgroundImage:"url(img/chara/c" + id + ".jpg)"});
		id=$(this).index();
		$('#cImage img').stop().fadeTo(200,0.0).eq(id).fadeTo(400,1).css({zIndex:"2"})
	})




	/*-------------------------------------------------------------------------------------*/
});
